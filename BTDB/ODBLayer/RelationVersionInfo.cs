using System;
using System.Collections.Generic;
using System.Linq;
using BTDB.FieldHandler;
using BTDB.KVDBLayer;
using BTDB.StreamLayer;

namespace BTDB.ODBLayer
{
    internal struct FieldId : IEquatable<FieldId>
    {
        readonly bool _isFromPrimaryKey;
        readonly uint _index;

        public bool IsFromPrimaryKey => _isFromPrimaryKey;
        public uint Index => _index;

        public FieldId(bool isFromPrimaryKey, uint index)
        {
            _isFromPrimaryKey = isFromPrimaryKey;
            _index = index;
        }

        public bool Equals(FieldId other)
        {
            return _isFromPrimaryKey == other.IsFromPrimaryKey && _index == other.Index;
        }
    }

    internal class SecondaryKeyInfo
    {
        public IList<FieldId> Fields { get; set; }
        public uint Index { get; set; }  //todo index assigning
        public string Name { get; set; }
    }

    class RelationVersionInfo
    {
        readonly IList<TableFieldInfo> _primaryKeyFields;  //field info
        IDictionary<string, uint> _secondaryKeysNames;
        IDictionary<uint, SecondaryKeyInfo> _secondaryKeys; 

        readonly TableFieldInfo[] _fields;


        public RelationVersionInfo(Dictionary<uint, TableFieldInfo> primaryKeyFields,  //order -> info
                                   Dictionary<uint, IList<SecondaryKeyAttribute>> secondaryKeys,  //value field idx -> attrs
                                   TableFieldInfo[] fields, uint firstIndex)
        {
            _primaryKeyFields = primaryKeyFields.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
            CreateSecondaryKeyInfo(secondaryKeys, primaryKeyFields, firstIndex);
            _fields = fields;
        }

        void CreateSecondaryKeyInfo(Dictionary<uint, IList<SecondaryKeyAttribute>> attributes, 
                                    Dictionary<uint, TableFieldInfo> primaryKeyFields, uint firstIndex)
        {
            var idx = 0u;
            _secondaryKeys = new Dictionary<uint, SecondaryKeyInfo>();
            _secondaryKeysNames = new Dictionary<string, uint>();
            var skIndexNames = attributes.SelectMany(kv => kv.Value).Select(a => a.Name).Distinct();
            foreach (var indexName in skIndexNames)
            {
                var indexFields = new List<Tuple<uint, SecondaryKeyAttribute>>();  //fieldIndex, attribute
                foreach (var kv in attributes)
                {
                    var attr = kv.Value.FirstOrDefault(a => a.Name == indexName);
                    if (attr == null)
                        continue;
                    indexFields.Add(Tuple.Create(kv.Key, attr));
                }
                var orderedAttrs = indexFields.OrderBy(a => a.Item2.Order).ToList();
                var info = new SecondaryKeyInfo
                {
                    Name = indexName,
                    Fields = new List<FieldId>(),
                    Index = firstIndex++
                };
                foreach (var attr in orderedAttrs)
                {
                    info.Fields.Add(new FieldId(false, attr.Item1));
                    if (attr.Item2.IncludePrimaryKeyOrder != default(uint))
                    {
                        var pi = _primaryKeyFields.IndexOf(primaryKeyFields[attr.Item2.IncludePrimaryKeyOrder]);
                        info.Fields.Add(new FieldId(true, (uint)pi));
                    }
                }
                _secondaryKeysNames[indexName] = idx;
                _secondaryKeys[idx++] = info;
            }
        }

        RelationVersionInfo(IList<TableFieldInfo> primaryKeyFields,
                            Dictionary<uint, SecondaryKeyInfo> secondaryKeys,
                            Dictionary<string, uint> secondaryKeysNames,
                            TableFieldInfo[] fields)
        {
            _primaryKeyFields = primaryKeyFields;
            _secondaryKeys = secondaryKeys;
            _fields = fields;
        }

        internal TableFieldInfo this[string name]
        {
            get { return _fields.Concat(_primaryKeyFields).FirstOrDefault(tfi => tfi.Name == name); }
        }

        internal IReadOnlyCollection<TableFieldInfo> GetValueFields()
        {
            return _fields;
        }

        internal IReadOnlyCollection<TableFieldInfo> GetPrimaryKeyFields()
        {
            return (IReadOnlyCollection<TableFieldInfo>)_primaryKeyFields;
        }

        internal IReadOnlyCollection<TableFieldInfo> GetAllFields()
        {
            return _primaryKeyFields.Concat(_fields).ToList();
        }

        internal bool HasSecondaryIndexes => _secondaryKeys.Count > 0;

        internal IDictionary<uint, SecondaryKeyInfo> SecondaryKeys => _secondaryKeys;

        internal IReadOnlyCollection<TableFieldInfo> GetSecondaryKeyFields(uint secondaryKeyIndex)
        {
            SecondaryKeyInfo info;
            if (!_secondaryKeys.TryGetValue(secondaryKeyIndex, out info))
                throw new BTDBException($"Unknown secondary key {secondaryKeyIndex}.");
            var fields = new List<TableFieldInfo>();
            foreach (var field in info.Fields)
            {
                if (field.IsFromPrimaryKey)
                    fields.Add(_primaryKeyFields[(int)field.Index]);
                else
                    fields.Add(_fields[(int)field.Index]);
            }
            return fields;
        }

        public IReadOnlyCollection<TableFieldInfo> GetSecondaryKeyValueKeys(uint secondaryKeyIndex)
        {
            SecondaryKeyInfo info;
            if (!_secondaryKeys.TryGetValue(secondaryKeyIndex, out info))
                throw new BTDBException($"Unknown secondary key {secondaryKeyIndex}.");
            var fields = new List<TableFieldInfo>();
            for (int i = 0; i < _primaryKeyFields.Count; i++)
            {
                if (info.Fields.Any(f => f.IsFromPrimaryKey && f.Index == i))
                    continue; //do not put again into value fields present in secondary key index
                fields.Add(_primaryKeyFields[i]);
            }
            return fields;
        }

        internal uint GetSecondaryKeyIndex(string name)
        {
            uint index;
            if (!_secondaryKeysNames.TryGetValue(name, out index))
                throw new BTDBException($"Unknown secondary key {name}.");
            return index;
        }

        internal void Save(AbstractBufferedWriter writer)
        {
            writer.WriteVUInt32((uint)_primaryKeyFields.Count);
            foreach (var field in _primaryKeyFields)
            {
                field.Save(writer);
            }

            writer.WriteVUInt32((uint)_secondaryKeys.Count);
            foreach (var key in _secondaryKeysNames)
            {
                writer.WriteString(key.Key);
                writer.WriteVUInt32(key.Value);
            }
            foreach (var key in _secondaryKeys)
            {
                writer.WriteVUInt32(key.Key);
                var info = key.Value;
                writer.WriteVUInt32(info.Index);
                writer.WriteString(info.Name);
                writer.WriteVUInt32((uint)info.Fields.Count);
                foreach (var fi in info.Fields)
                {
                    writer.WriteBool(fi.IsFromPrimaryKey);
                    writer.WriteVUInt32(fi.Index);
                }
            }
            writer.WriteVUInt32((uint)_fields.Length);
            for (var i = 0; i < _fields.Length; i++)
            {
                _fields[i].Save(writer);
            }
        }

        public static RelationVersionInfo Load(AbstractBufferedReader reader, IFieldHandlerFactory fieldHandlerFactory, string relationName)
        {
            var pkCount = reader.ReadVUInt32();
            var primaryKeys = new List<TableFieldInfo>((int)pkCount);
            for (var i = 0u; i < pkCount; i++)
            {
                primaryKeys.Add(TableFieldInfo.Load(reader, fieldHandlerFactory, relationName));
            }

            var skCount = reader.ReadVUInt32();
            var secondaryKeys = new Dictionary<uint, SecondaryKeyInfo>((int)skCount);
            var secondaryKeysNames = new Dictionary<string, uint>((int)skCount);
            for (var i = 0; i < skCount; i++)
            {
                var name = reader.ReadString();
                secondaryKeysNames.Add(name, reader.ReadVUInt32());
            }
            for (var i = 0; i < skCount; i++)
            {
                var skIndex = reader.ReadVUInt32();
                var info = new SecondaryKeyInfo();
                info.Index = reader.ReadVUInt32();
                info.Name = reader.ReadString();
                var cnt = reader.ReadVUInt32();
                info.Fields = new List<FieldId>((int)cnt);
                for (var j = 0; j < cnt; j++)
                {
                    var fromPrimary = reader.ReadBool();
                    var index = reader.ReadVUInt32();
                    info.Fields.Add(new FieldId(fromPrimary, index));
                }
                secondaryKeys.Add(skIndex, info);
            }

            var fieldCount = reader.ReadVUInt32();
            var fieldInfos = new TableFieldInfo[fieldCount];
            for (int i = 0; i < fieldCount; i++)
            {
                fieldInfos[i] = TableFieldInfo.Load(reader, fieldHandlerFactory, relationName);
            }

            return new RelationVersionInfo(primaryKeys, secondaryKeys, secondaryKeysNames, fieldInfos);
        }

        internal bool NeedsCtx()
        {
            return _fields.Any(tfi => tfi.Handler.NeedsCtx());
        }

        internal bool NeedsInit()
        {
            return _fields.Any(tfi => tfi.Handler is IFieldHandlerWithInit);
        }

        internal bool NeedsFreeContent()
        {
            return _fields.Any(tfi => tfi.Handler is ODBDictionaryFieldHandler);
        }

        internal static bool Equal(RelationVersionInfo a, RelationVersionInfo b)
        {
            //PKs
            if (a._primaryKeyFields.Count != b._primaryKeyFields.Count) return false;
            for (int i = 0; i < a._primaryKeyFields.Count; i++)
            {
                if (!TableFieldInfo.Equal(a._primaryKeyFields[i], b._primaryKeyFields[i])) return false;
            }
            //SKs
            if (a._secondaryKeys.Count != b._secondaryKeys.Count) return false;
            foreach (var key in a._secondaryKeys)
            {
                SecondaryKeyInfo bInfo;
                if (!b._secondaryKeys.TryGetValue(key.Key, out bInfo)) return false;
                if (!key.Value.Equals(bInfo)) return false;
            }
            //Fields
            if (a._fields.Length != b._fields.Length) return false;
            for (int i = 0; i < a._fields.Length; i++)
            {
                if (!TableFieldInfo.Equal(a._fields[i], b._fields[i])) return false;
            }
            return true;
        }
    }
}