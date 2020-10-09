using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Rainbow.Filtering;
using Rainbow.Model;
using Rainbow.Storage.Yaml;
using Sitecore.Data.Engines;
using Unicorn.PowerShell.Sql;

namespace Unicorn.PowerShell
{
    public static class BulkItemExtractor
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

        private static YamlSerializationFormatter CreateFormatter(IFieldFilter filter)
        {
            // shut yer gob again :D
            var config = @"<serializationFormatter type=""Rainbow.Storage.Yaml.YamlSerializationFormatter, Rainbow.Storage.Yaml"" singleInstance=""true"">
					<fieldFormatter type=""Rainbow.Formatting.FieldFormatters.MultilistFormatter, Rainbow"" />
					<fieldFormatter type=""Rainbow.Formatting.FieldFormatters.XmlFieldFormatter, Rainbow"" />
					<fieldFormatter type=""Rainbow.Formatting.FieldFormatters.CheckboxFieldFormatter, Rainbow"" />
				</serializationFormatter>";

            var doc = new XmlDocument();
            doc.LoadXml(config);

            return new YamlSerializationFormatter(doc.DocumentElement, filter);
        }

        private static string ProcessIItemData(IItemData item)
        {
            if (item == null) return null;

            var formatter = CreateFormatter(CreateFieldFilter());

            using (var stream = new MemoryStream())
            {
                formatter.WriteSerializedItem(item, stream);

                stream.Seek(0, SeekOrigin.Begin);

                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static List<string> ItemExtractor(BlockingCollection<IItemData> itemsToExtract, CancellationToken cancellationToken)
        {
            Thread.CurrentThread.Priority = ThreadPriority.Lowest;

            var yamlItems = new List<string>();
            using (new SyncOperationContext())
            {
                while (!itemsToExtract.IsCompleted)
                {
                    if (!itemsToExtract.TryTake(out var item, -1))
                    {
                        break;
                    }

                    var yaml = ProcessIItemData(item);
                    if (!string.IsNullOrEmpty(yaml))
                    {
                        yamlItems.Add(yaml);
                    }
                }
            }

            return yamlItems;
        }
        
        public static string[] LoadItems(string rootId, string[] itemIds)
        {
            if (string.IsNullOrEmpty(rootId)) return null;
            if (itemIds == null) return null;

            var db = Sitecore.Configuration.Factory.GetDatabase("master");
            var item = db.GetItem(rootId);
            if (item == null) return null;

            var rootParentItemPath = item.Parent.Paths.Path;

            var cancellationToken = new CancellationToken();

            var yamlItems = new List<string>();
            var bulkItemExtractor = new SqlItemExtractor("master");
            var extractedItems = bulkItemExtractor.ExtractItems(Guid.Parse(rootId), rootParentItemPath, itemIds.Select(Guid.Parse).ToArray(), (IEnumerableFieldFilter)CreateFieldFilter());

            var itemsToExtract = new BlockingCollection<IItemData>();
            foreach (var extractedItem in extractedItems)
            {
                itemsToExtract.Add(extractedItem, cancellationToken);
            }
            itemsToExtract.CompleteAdding();

            var threads = itemsToExtract.Count > 3 ? 8 : 1;
            var running = new List<Task<List<string>>>();
            for (var i = 0; i < threads; i++)
            {
                running.Add(Task.Run(() => ItemExtractor(itemsToExtract, cancellationToken), cancellationToken));
            }
            
            foreach (var t in running)
            {
                t.Wait(cancellationToken);
                yamlItems.AddRange(t.Result);
            }

            return yamlItems.ToArray();
        }
    }
}
