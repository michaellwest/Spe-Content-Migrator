using System;
using System.Collections.Generic;
using System.Diagnostics;
using Rainbow.Model;
using Unicorn.Data.Dilithium.Sql;

namespace Unicorn.PowerShell.Sql
{
    [DebuggerDisplay("{Name} ({DatabaseName}::{Id}) [DILITHIUM SQL]")]
    public class CustomSqlItemData : IItemData
    {
        public Guid Id { get; set; }

        public Guid ParentId { get; set; }

        public Guid TemplateId { get; set; }

        public string Path { get; set; }

        public string SerializedItemId => "(from Sitecore Database [Dilithium])";

        public string DatabaseName { get; set; }

        public string Name { get; set; }

        public Guid BranchId { get; set; }

        public IEnumerable<IItemFieldValue> SharedFields => RawSharedFields;

        public IEnumerable<IItemLanguage> UnversionedFields => RawUnversionedFields;

        public IEnumerable<IItemVersion> Versions => RawVersions;

        public IEnumerable<IItemData> GetChildren()
        {
            return new List<IItemData>();
        }

        // the predicate tree root item ID this item was sourced for
        public IList<SqlItemFieldValue> RawSharedFields { get; } = new List<SqlItemFieldValue>();
        public IList<SqlItemLanguage> RawUnversionedFields { get; } = new List<SqlItemLanguage>();
        public IList<SqlItemVersion> RawVersions { get; } = new List<SqlItemVersion>();
    }
}
