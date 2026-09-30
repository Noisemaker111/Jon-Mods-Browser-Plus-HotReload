using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
namespace JonLootSkulls
{
    public sealed class LootRecord
    {
        public string Key;
        public int X, Y, Z, PoiId;
        public float AnchorX, AnchorY, AnchorZ;
        public int TouchedHours;
    }

    public sealed class LootLedger
    {
        public readonly Dictionary<string, LootRecord> Records = new Dictionary<string, LootRecord>(StringComparer.Ordinal);
        public void Load(string file)
        {
            Records.Clear();
            if (!File.Exists(file)) return;
            using (var input = File.OpenRead(file))
                foreach (var record in (List<LootRecord>)new XmlSerializer(typeof(List<LootRecord>)).Deserialize(input))
                    Records[record.Key] = record;
        }
        public void Save(string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temporary = file + ".tmp";
            try
            {
                using (var output = File.Create(temporary))
                    new XmlSerializer(typeof(List<LootRecord>)).Serialize(output, new List<LootRecord>(Records.Values));
                if (File.Exists(file)) File.Replace(temporary, file, file + ".bak");
                else File.Move(temporary, file);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public void ClearPoi(int id)
        {
            var remove = new List<string>();
            foreach (var pair in Records) if (pair.Value.PoiId == id) remove.Add(pair.Key);
            foreach (var key in remove) Records.Remove(key);
        }
    }

}
