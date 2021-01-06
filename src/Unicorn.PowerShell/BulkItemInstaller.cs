using Rainbow.Filtering;
using Rainbow.Model;
using Rainbow.Storage.Sc.Deserialization;
using Sitecore.Configuration;
using Sitecore.Data;
using Sitecore.Data.Engines;
using Sitecore.Data.Events;
using Sitecore.Diagnostics;
using Sitecore.SecurityModel;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Unicorn.Deserialization;
using Unicorn.Logging;

namespace Unicorn.PowerShell
{
    public static class BulkItemInstaller
    {
        private static IFieldFilter CreateFieldFilter()
        {
            // Updated to allow Revision to come back.
            var config = @"<fieldFilter type=""Rainbow.Filtering.ConfigurationFieldFilter, Rainbow"" singleInstance=""true"">
					<exclude fieldID=""{B1E16562-F3F9-4DDD-84CA-6E099950ECC0}"" note=""'Last run' field on Schedule template (used to register tasks)"" />
					<exclude fieldID=""{52807595-0F8F-4B20-8D2A-CB71D28C6103}"" note=""'__Owner' field on Standard Template"" />
					<exclude fieldID=""{F6D8A61C-2F84-4401-BD24-52D2068172BC}"" note=""'__Originator' field on Standard Template"" />
					<exclude fieldID=""{D9CF14B1-FA16-4BA6-9288-E8A174D4D522}"" note=""'__Updated' field on Standard Template"" />
					<exclude fieldID=""{BADD9CF9-53E0-4D0C-BCC0-2D784C282F6A}"" note=""'__Updated by' field on Standard Template"" />
					<exclude fieldID=""{001DD393-96C5-490B-924A-B0F25CD9EFD8}"" note=""'__Lock' field on Standard Template"" />
				</fieldFilter>";


            var doc = new XmlDocument();
            doc.LoadXml(config);

            return new ConfigurationFieldFilter(doc.DocumentElement);
        }

        private static bool ProcessItem(IItemData item)
        {
            if (item == null) return false;

            var consoleLogger = new SitecoreLogger();
            var deserializer = new DefaultDeserializer(false, new DefaultDeserializerLogger(consoleLogger), CreateFieldFilter());

            consoleLogger.Info(item.Path);

            try
            {
                using (new SettingsSwitcher("AllowDuplicateItemNamesOnSameLevel", "true"))
                {
                    deserializer.Deserialize(item, null);
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to install item {item.Path}", ex, typeof(BulkItemInstaller));

                return false;
            }
        }

        private static int ItemInstaller(BlockingCollection<IItemData> itemsToInstall, CancellationToken cancellationToken)
        {
            Thread.CurrentThread.Priority = ThreadPriority.Lowest;
            var successCount = 0;
            using (new BulkUpdateContext())
            using (new EventDisabler())
            using (new SecurityDisabler())
            using (new SyncOperationContext())
            using (new DatabaseCacheDisabler())
            {
                while (!itemsToInstall.IsCompleted)
                {
                    if (!itemsToInstall.TryTake(out var item, -1))
                    {
                        break;
                    }
                    successCount += ProcessItem(item) ? 1 : 0;
                }
            }

            return successCount;
        }

        public static int LoadItems(IItemData[] items)
        {
            if (items == null) return 0;

            var cancellationToken = new CancellationToken();
            var itemsToInstall = new BlockingCollection<IItemData>();
            foreach (var item in items)
            {
                itemsToInstall.Add(item, cancellationToken);
            }
            itemsToInstall.CompleteAdding();

            var threads = itemsToInstall.Count > 3 ? 8 : 1;
            var running = new List<Task<int>>();
            for (var i = 0; i < threads; i++)
            {
                running.Add(Task.Run(() => ItemInstaller(itemsToInstall, cancellationToken), cancellationToken));
            }

            var successCount = 0;
            foreach (var t in running)
            {
                t.Wait(cancellationToken);
                successCount += t.Result;
            }

            return successCount;
        }
    }
}