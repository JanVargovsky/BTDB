using System.Collections.Generic;
using System.Linq;
using BTDB.FieldHandler;
using BTDB.KVDBLayer;
using BTDB.StreamLayer;

namespace BTDB.ODBLayer
{
    class RelationVersionInfo
    {
        readonly IDictionary<uint, TableFieldInfo> _primaryKeys;
        readonly IDictionary<uint, SecondaryKeyAttribute> _secondaryKeysInfo;
        readonly TableFieldInfo[] _fields;


        public RelationVersionInfo(Dictionary<uint, TableFieldInfo> primaryKeys,
                                   Dictionary<uint, SecondaryKeyAttribute> secondaryKeysInfo,
                                   TableFieldInfo[] fields)
        {
            _primaryKeys = primaryKeys;
            _secondaryKeysInfo = secondaryKeysInfo;
            _fields = fields;
        }

        internal int FieldCount => _fields.Length;

        internal TableFieldInfo this[int idx] => _fields[idx];

        internal TableFieldInfo this[string name]
        {
            get { return _fields.FirstOrDefault(tfi => tfi.Name == name); }
        }

        internal void Save(AbstractBufferedWriter writer)
        {
            writer.WriteVUInt32((uint)_primaryKeys.Count);
            foreach (var key in _primaryKeys)
            {
                writer.WriteVUInt32(key.Key);
                key.Value.Save(writer);
            }
            writer.WriteVUInt32((uint)_secondaryKeysInfo.Count);
            foreach (var key in _secondaryKeysInfo)
            {
                writer.WriteVUInt32(key.Key);
                writer.WriteString(key.Value.Name);
                writer.WriteVUInt32(key.Value.Order);
                writer.WriteVUInt32(key.Value.IncludePrimaryKeyOrder);
            }
            writer.WriteVUInt32((uint)FieldCount);
            for (int i = 0; i < FieldCount; i++)
            {
                this[i].Save(writer);
            }
        }

        public static RelationVersionInfo Load(AbstractBufferedReader reader, IFieldHandlerFactory fieldHandlerFactory, string relationName)
        {
            var pkCount = reader.ReadVUInt32();
            var primaryKeys = new Dictionary<uint, TableFieldInfo>();
            for (var i = 0; i < pkCount; i++)
            {
                var order = reader.ReadVUInt32();
                primaryKeys[order] = TableFieldInfo.Load(reader, fieldHandlerFactory, relationName);
            }

            var skCount = reader.ReadVUInt32();
            var secondaryKeysInfo = new Dictionary<uint, SecondaryKeyAttribute>();
            for (var i = 0; i < skCount; i++)
            {
                var fieldId = reader.ReadVUInt32();
                var attribute = new SecondaryKeyAttribute(reader.ReadString());
                attribute.Order = reader.ReadVUInt32();
                attribute.IncludePrimaryKeyOrder = reader.ReadVUInt32();
                secondaryKeysInfo.Add(fieldId, attribute);
            }

            var fieldCount = reader.ReadVUInt32();
            var fieldInfos = new TableFieldInfo[fieldCount];
            for (int i = 0; i < fieldCount; i++)
            {
                fieldInfos[i] = TableFieldInfo.Load(reader, fieldHandlerFactory, relationName);
            }

            return new RelationVersionInfo(primaryKeys, secondaryKeysInfo, fieldInfos);
        }

        internal bool NeedsCtx()
        {
            return _fields.Any(tfi => tfi.Handler.NeedsCtx());
        }

        internal bool NeedsInit()
        {
            return _fields.Any(tfi => tfi.Handler is IFieldHandlerWithInit);
        }

        internal static bool Equal(RelationVersionInfo a, RelationVersionInfo b)
        {
            if (a._primaryKeys.Count != b._primaryKeys.Count) return false;
            foreach (var key in a._primaryKeys)
            {
                TableFieldInfo bvalue;
                if (!b._primaryKeys.TryGetValue(key.Key, out bvalue)) return false;
                if (!TableFieldInfo.Equal(key.Value, bvalue)) return false;
            }

            if (a._secondaryKeysInfo.Count != b._secondaryKeysInfo.Count) return false;
            foreach (var key in a._secondaryKeysInfo)
            {
                SecondaryKeyAttribute battribute;
                if (!b._secondaryKeysInfo.TryGetValue(key.Key, out battribute)) return false;
                if (!SecondaryKeyAttribute.Equal(key.Value, battribute)) return false;
            }

            if (a.FieldCount != b.FieldCount) return false;
            for (int i = 0; i < a.FieldCount; i++)
            {
                if (!TableFieldInfo.Equal(a[i], b[i])) return false;
            }
            return true;
        }
    }
}