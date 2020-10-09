using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Rainbow.Filtering;
using Rainbow.Model;
using Sitecore.Data;
using Sitecore.Data.Managers;
using Sitecore.Data.Templates;
using Sitecore.Diagnostics;
using Unicorn.Data.Dilithium.Sql;

namespace Unicorn.PowerShell.Sql
{
    public class SqlItemExtractor
    {
        protected object SyncLock = new object();

        private Dictionary<Guid, CustomSqlItemData> _itemsById;

        private readonly Dictionary<Guid, TemplateField> _templateMetadataLookup = new Dictionary<Guid, TemplateField>(1000);

        public SqlItemExtractor(string databaseName)
        {
            Database = Database.GetDatabase(databaseName);
            Assert.ArgumentNotNull(Database, nameof(databaseName));
        }

        public Database Database { get; }
        
        private TemplateField GetTemplateField(Guid fieldId, Guid templateId)
        {
            TemplateField result;
            if (_templateMetadataLookup.TryGetValue(fieldId, out result)) return result;

            var candidateField = TemplateManager.GetTemplateField(new ID(fieldId), new ID(templateId), Database);

            if (candidateField != null) return _templateMetadataLookup[fieldId] = candidateField;

            // if we got here it probably means that there's a field value in the DB that is from the _wrong_ template ID
            // Sitecore seems to ignore this when it occurs, so so will we - we'll skip loading the field
            return null;
        }

        private bool SetSharedField(CustomSqlItemData targetItem, SqlItemFieldValue currentField, int version, string language)
        {
            // check for corruption in SQL server tables (field values in wrong table) - shared field should have neither language nor version one or greater (SQL sends version -1 for shared)
            if (version >= 1)
            {
                Log.Error($"[Dilithium] Data corruption in {targetItem.DatabaseName}://{{{targetItem.Id}}}! Field {{{currentField.FieldId}}} (shared) had a value in the versioned fields table. The field value will be ignored.", this);
                return false;
            }

            if (!string.IsNullOrEmpty(language))
            {
                Log.Error($"[Dilithium] Data corruption in {targetItem.DatabaseName}://{{{targetItem.Id}}}! Field {currentField.FieldId} (shared) had a value in the unversioned fields table. The field value will be ignored.", this);
                return false;
            }

            targetItem.RawSharedFields.Add(currentField);

            return true;
        }

        private bool SetUnversionedField(CustomSqlItemData targetItem, SqlItemFieldValue currentField, int version, string language)
        {
            // check for corruption in SQL server tables (field values in wrong table) - an unversioned field should have a version less than 1 (SQL sends -1 back for unversioned) and a language
            if (version >= 1)
            {
                Log.Error($"[Dilithium] Data corruption in {targetItem.DatabaseName}://{{{targetItem.Id}}}! Field {currentField.FieldId} (unversioned) had a value in the versioned fields table. The field value will be ignored.", this);
                return false;
            }

            if (string.IsNullOrEmpty(language))
            {
                Log.Error($"[Dilithium] Data corruption in {targetItem.DatabaseName}://{{{targetItem.Id}}}! Field {currentField.FieldId} (unversioned) had a value in the shared fields table. The field value will be ignored.", this);
                return false;
            }

            foreach (var languageFields in targetItem.RawUnversionedFields)
            {
                if (languageFields.Language.Name.Equals(language, StringComparison.Ordinal))
                {
                    languageFields.RawFields.Add(currentField);
                    return true;
                }
            }

            var newLanguage = new SqlItemLanguage();
            newLanguage.Language = new CultureInfo(language);
            newLanguage.RawFields.Add(currentField);

            targetItem.RawUnversionedFields.Add(newLanguage);

            return true;
        }

        private bool SetVersionedField(CustomSqlItemData targetItem, string language, int version, SqlItemFieldValue currentField)
        {
            // check for corruption in SQL server tables (field values in wrong table) - a versioned field should have both a language and a version that's one or greater
            if (version < 1)
            {
                if (string.IsNullOrEmpty(language))
                {
                    Log.Error($"[Dilithium] Data corruption in {targetItem.DatabaseName}://{{{targetItem.Id}}}! Field {currentField.FieldId} (versioned) had a value in the shared fields table. The field value will be ignored.", this);
                }
                else
                {
                    Log.Error($"[Dilithium] Data corruption in {targetItem.DatabaseName}://{{{targetItem.Id}}}! Field {currentField.FieldId} (versioned) had a value in the unversioned fields table. The field value will be ignored.", this);
                }
                return false;
            }

            foreach (var versionFields in targetItem.RawVersions)
            {
                if (versionFields.Language.Name.Equals(language, StringComparison.Ordinal) && versionFields.VersionNumber == version)
                {
                    versionFields.RawFields.Add(currentField);
                    return true;
                }
            }

            var newVersion = new SqlItemVersion
            {
                Language = new CultureInfo(language), 
                VersionNumber = version
            };
            newVersion.RawFields.Add(currentField);

            targetItem.RawVersions.Add(newVersion);

            return true;
        }

        private bool IngestFieldData(SqlDataReader reader)
        {
            var errors = false;

            // the reader will be on result set 0 when it arrives (item data)
            // so we need to advance it to set 1 (descendants field data)
            reader.NextResult();

            var itemsById = _itemsById;

            while (reader.Read())
            {
                var itemId = reader.GetGuid(0);
                var language = reader.GetString(1);
                var version = reader.GetInt32(4);

                var currentField = new SqlItemFieldValue(itemId, Database.Name, language, version)
                {
                    FieldId = reader.GetGuid(2), Value = reader.GetString(3)
                };


                // get current item to add fields to
                if (!itemsById.TryGetValue(itemId, out var targetItem)) throw new InvalidOperationException($"Item {itemId} was not read by the item loader but had field {currentField.FieldId} in the field loader!");

                var fieldMetadata = GetTemplateField(currentField.FieldId, targetItem.TemplateId);

                if (fieldMetadata == null)
                {
                    // if we got here it probably means that there's a field value in the DB that is from the _wrong_ template ID
                    // Sitecore seems to ignore this when it occurs, so so will we - we'll skip loading the field
                    continue;
                }

                currentField.NameHint = fieldMetadata.Name;
                currentField.FieldType = fieldMetadata.Type;

                // for blob fields we need to set the blob ID so it can be read
                if (fieldMetadata.IsBlob)
                {
                    var blobCandidateValue = currentField.Value;
                    if (blobCandidateValue.Length > 38)
                        blobCandidateValue = blobCandidateValue.Substring(0, 38);

                    if (ID.TryParse(blobCandidateValue, out var blobId)) currentField.BlobId = blobId.Guid;
                }

                // add field to target item data
                if (fieldMetadata.IsShared)
                {
                    // shared field = no version, no language
                    if (!SetSharedField(targetItem, currentField, version, language)) errors = true;
                }
                else if (fieldMetadata.IsUnversioned)
                {
                    // unversioned field = no version, with language (version -1 is used as a nonversioned flag)
                    if (!SetUnversionedField(targetItem, currentField, version, language)) errors = true;
                }
                else
                {
                    // versioned field
                    if (!SetVersionedField(targetItem, language, version, currentField)) errors = true;
                }
            }

            return errors;
        }

        private void IngestItemData(SqlDataReader reader, string rootParentItemPath)
        {
            // 8087 = prime. Lots of items will load in so we start with a large capacity to minimize expansions.
            // Dictionary expands using primes, hence our choice.
            var results = new Dictionary<Guid, CustomSqlItemData>(8087);

            while (reader.Read())
            {
                var currentItem = new CustomSqlItemData
                {
                    Id = reader.GetGuid(0),
                    Name = reader.GetString(1),
                    TemplateId = reader.GetGuid(2),
                    BranchId = reader.GetGuid(3),
                    ParentId = reader.GetGuid(4),
                    Path = rootParentItemPath + reader.GetString(5),
                    DatabaseName = Database.Name
                };

                results.Add(currentItem.Id, currentItem);
            }

            _itemsById = results;
        }

        private bool Ingest(SqlDataReader reader, string rootParentItemPath)
        {
            IngestItemData(reader, rootParentItemPath);

            var readDataTask = Task.Run(() => IngestFieldData(reader));

            readDataTask.Wait();

            return !readDataTask.Result;
        }

        public IItemData[] ExtractItems(Guid rootId, string rootParentItemPath, Guid[] itemIds, IEnumerableFieldFilter fieldFilter)
        {
            lock (SyncLock)
            {
                var timer = new Stopwatch();
                timer.Start();

                var intersectedIgnoredFields = new HashSet<Guid>();
                intersectedIgnoredFields.UnionWith(fieldFilter.Excludes);

                if (itemIds.Length == 0) return null;

                using (var sqlConnection = new SqlConnection(ConfigurationManager.ConnectionStrings["master"].ConnectionString))
                {
                    sqlConnection.Open();
                    using (var sqlCommand = ConstructSqlBatch(rootId, itemIds, intersectedIgnoredFields?.ToArray()))
                    {
                        sqlCommand.Connection = sqlConnection;

                        using (var reader = sqlCommand.ExecuteReader())
                        {
                            if (!Ingest(reader, rootParentItemPath))
                            {
                                // TODO: Log error
                            }
                        }
                    }
                }


                timer.Stop();

                return _itemsById.Values.ToArray() as IItemData[];
            }
        }

        private SqlCommand ConstructSqlBatch(Guid rootId, Guid[] rootItemIds, Guid[] ignoredFields)
        {
            Assert.ArgumentNotNull(rootItemIds, nameof(rootItemIds));
            if (rootItemIds.Length == 0) throw new InvalidOperationException("Cannot make a query for empty root set. This likely means a predicate did not have any roots.");
            if (ignoredFields == null) ignoredFields = new Guid[0];

            var command = new SqlCommand();
            var debugCommand = new StringBuilder();

            // add parameters for ignored fields
            var ignoredFieldsInStatement = BuildSqlInStatement(ignoredFields, command, "i", debugCommand);

            var ignoredFieldsValueSkipStatement = $@"CASE WHEN FieldID {ignoredFieldsInStatement} THEN '' ELSE Value END AS Value";

            // add parameters for root item IDs
            var rootItemIdsInStatement = BuildSqlInStatement(rootItemIds);

            var sql = new StringBuilder(8000);

            // ITEM DATA QUERY - gets top level metadata about included items (no fields)
            sql.Append($@"
                IF OBJECT_ID('tempdb..#TempItemData') IS NOT NULL DROP Table #TempItemData

                CREATE TABLE #TempItemData(
	                ID uniqueidentifier,
	                Name nvarchar(256),
	                TemplateID uniqueidentifier,
	                MasterID uniqueidentifier,
	                ParentID uniqueidentifier,
	                ItemPath nvarchar(MAX)
                );

                WITH ItemsTable (ID, Name, TemplateID, MasterID, ParentID, ItemPath)
                AS
                (
                    SELECT base.ID, base.Name, base.TemplateID, base.MasterID, base.ParentID, CAST('/' + base.Name AS nvarchar(MAX)) as ItemPath			
                    FROM Items as base
                    WHERE base.ID = '{rootId}'	

                    UNION ALL
	                
                    SELECT child.ID, child.Name, child.TemplateID, child.MasterID, child.ParentID, CAST(ItemPath + '/' + child.Name AS nvarchar(MAX))			
                    FROM ItemsTable as parent 
                    INNER JOIN Items as child 
                        ON child.ParentID = parent.ID 
                )	

                INSERT INTO #TempItemData
                SELECT *
                FROM ItemsTable
                WHERE ID {rootItemIdsInStatement}

                SELECT ID, Name, TemplateID, MasterID, ParentID, ItemPath
                FROM #TempItemData
");

            // FIELDS DATA QUERY - gets all fields for all languages and versions of the root items and all descendants
            sql.Append($@"
				SELECT ItemId, '' AS Language, FieldId, {ignoredFieldsValueSkipStatement}, -1 as Version
				FROM SharedFields s
				INNER JOIN #TempItemData t ON s.ItemId = t.ID
				UNION ALL
				SELECT ItemId, Language, FieldId, {ignoredFieldsValueSkipStatement}, -1 as Version
				FROM UnversionedFields u
				INNER JOIN #TempItemData t ON u.ItemId = t.ID
				UNION ALL
				SELECT ItemId, Language, FieldId, {ignoredFieldsValueSkipStatement}, Version
				FROM VersionedFields v
				INNER JOIN #TempItemData t ON v.ItemId = t.ID
");

            command.CommandText = sql.ToString();

            debugCommand.Append(sql);

            // drop a debugger on this to see a runnable SQL statement for SSMS
            var debugSqlStatement = debugCommand.ToString();

            return command;
        }

        private StringBuilder BuildSqlInStatement(Guid[] parameters, SqlCommand command, string parameterPrefix, StringBuilder debugStatementBuilder)
        {
            object currentParameter;
            string parameterName;

            var parameterNames = new List<string>(parameters.Length);

            for (int index = 0; index < parameters.Length; index++)
            {
                currentParameter = parameters[index];
                parameterName = parameterPrefix + index;

                command.Parameters.AddWithValue(parameterName, currentParameter);
                debugStatementBuilder.AppendLine($"DECLARE @{parameterName} UNIQUEIDENTIFIER = '{currentParameter}'");
                parameterNames.Add(parameterName);
            }

            var inStatement = new StringBuilder(((parameterPrefix.Length + 4) * parameters.Length) + 5); // ((prefixLength + '@, ') * paramCount) + 'IN ()'
            inStatement.Append("IN (");
            inStatement.Append("@"); // first element param @, subsequent get from join below
            inStatement.Append(string.Join(", @", parameterNames));
            inStatement.Append(")");

            return inStatement;
        }

        private StringBuilder BuildSqlInStatement(Guid[] parameters)
        {
            var inStatement = new StringBuilder();
            inStatement.Append("IN (");
            inStatement.Append(string.Join(",", parameters.Select(p => "'" + p + "'")));
            inStatement.Append(")");

            return inStatement;
        }
    }
}
