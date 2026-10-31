using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace TypeTrail {
    // Compression stays inside the current-user DPAPI envelope. Record metadata
    // cannot force the character budget to silently shrink to a fixed card count.
    internal static class HistoryArchiveCodec {
        static readonly byte[] Magic={0x54,0x54,0x48,4,13,10,26,0};
        const int MaxRecords=500000,MaxTextUnits=500000,MaxArchiveBytes=64*1024*1024;
        const int MaxSources=65536,MaxSourceUnits=4*1024*1024;
        public static bool IsCompact(byte[] value) {
            if(value==null || value.Length<Magic.Length)return false;
            for(int i=0;i<Magic.Length;i++)if(value[i]!=Magic[i])return false;
            return true;
        }
        static void Text(BinaryWriter writer,string value,int cap) {
            value=value??"";if(value.Length>cap)throw new InvalidDataException("History string exceeds budget");
            writer.Write(value.Length);
            // Preserve Windows UTF-16 code units, including an incomplete packet
            // retained for recovery; UTF-8 replacement fallback would change it.
            var bytes=new byte[value.Length*2];
            for(int i=0;i<value.Length;i++){bytes[i*2]=(byte)value[i];bytes[i*2+1]=(byte)(value[i]>>8);}
            writer.Write(bytes);Array.Clear(bytes,0,bytes.Length);
        }
        static string Text(BinaryReader reader,int cap) {
            int count=reader.ReadInt32();if(count<0 || count>cap)throw new InvalidDataException("Invalid history string length");
            byte[] bytes=reader.ReadBytes(count*2);if(bytes.Length!=count*2)throw new EndOfStreamException();
            var chars=new char[count];for(int i=0;i<count;i++)chars[i]=(char)(bytes[i*2]|bytes[i*2+1]<<8);
            string value=new string(chars);Array.Clear(bytes,0,bytes.Length);Array.Clear(chars,0,chars.Length);return value;
        }
        static void AddBudget(ref int units,string value) {
            units=checked(units+(value??"").Length);if(units>MaxTextUnits)throw new InvalidDataException("History character budget exceeded");
        }
        public static byte[] Encode(List<Entry> entries) {
            entries=entries??new List<Entry>();
            var names=new List<string>{"Application"};var indexes=new Dictionary<string,int>(StringComparer.Ordinal){{"Application",0}};
            int records=0,sourceUnits=names[0].Length;
            foreach(var entry in entries) {
                if(entry==null || !entry.HasContent)continue;
                if(++records>MaxRecords)throw new InvalidDataException("History record budget exceeded");
                string name=entry.Source??"Application";
                if(!indexes.ContainsKey(name) && name.Length<=1024 && names.Count<MaxSources && sourceUnits+name.Length<=MaxSourceUnits){indexes[name]=names.Count;names.Add(name);sourceUnits+=name.Length;}
            }
            using(var raw=new MemoryStream()) {
                try {
                using(var writer=new BinaryWriter(raw,Encoding.UTF8,true)) {
                    writer.Write(names.Count);foreach(string name in names)Text(writer,name,1024);
                    writer.Write(records);int units=0;
                    foreach(var entry in entries) {
                        if(entry==null || !entry.HasContent)continue;
                        int sourceIndex;writer.Write(indexes.TryGetValue(entry.Source??"Application",out sourceIndex)?sourceIndex:0);
                        Guid id;
                        if(Guid.TryParseExact(entry.Id,"N",out id) && entry.Id==id.ToString("N")){writer.Write((byte)1);writer.Write(id.ToByteArray());}
                        else if(entry.Id!=null && entry.Id.Length<=64){writer.Write((byte)2);Text(writer,entry.Id,64);}
                        else writer.Write((byte)0);
                        writer.Write(entry.Created.ToBinary());writer.Write(entry.Updated.ToBinary());
                        writer.Write((byte)((entry.PreviousVersion?1:0)|(entry.InputOnly?2:0)|(entry.Truncated?4:0)));
                        AddBudget(ref units,entry.Text);Text(writer,entry.Text,MaxTextUnits);
                        int revisions=0;
                        if(entry.Versions!=null)foreach(var version in entry.Versions)if(version!=null && !String.IsNullOrEmpty(version.Text))revisions++;
                        if(revisions>8)throw new InvalidDataException("History revision budget exceeded");
                        writer.Write((byte)revisions);
                        if(entry.Versions!=null)foreach(var version in entry.Versions) {
                            if(version==null || String.IsNullOrEmpty(version.Text))continue;
                            writer.Write(version.Saved.ToBinary());AddBudget(ref units,version.Text);Text(writer,version.Text,MaxTextUnits);
                        }
                        if(raw.Length>MaxArchiveBytes)throw new InvalidDataException("History metadata budget exceeded");
                    }
                }
                raw.Position=0;
                using(var packed=new MemoryStream()) {
                    packed.Write(Magic,0,Magic.Length);
                    using(var deflate=new DeflateStream(packed,CompressionLevel.Optimal,true))raw.CopyTo(deflate);
                    return packed.ToArray();
                }
                }finally{Array.Clear(raw.GetBuffer(),0,(int)raw.Length);}
            }
        }
        public static List<Entry> Decode(byte[] packed) {
            if(!IsCompact(packed) || packed.Length>MaxArchiveBytes)throw new InvalidDataException("Invalid history archive");
            using(var source=new MemoryStream(packed,Magic.Length,packed.Length-Magic.Length,false))
            using(var deflate=new DeflateStream(source,CompressionMode.Decompress))
            using(var raw=new MemoryStream()) {
                var buffer=new byte[8192];int read;
                try {
                    while((read=deflate.Read(buffer,0,buffer.Length))>0){if(raw.Length+read>MaxArchiveBytes)throw new InvalidDataException("History expansion budget exceeded");raw.Write(buffer,0,read);}
                    raw.Position=0;
                    using(var reader=new BinaryReader(raw,Encoding.UTF8,true)) {
                        int sourceCount=reader.ReadInt32();if(sourceCount<1 || sourceCount>MaxSources)throw new InvalidDataException("Invalid history sources");
                        var names=new string[sourceCount];int sourceUnits=0;
                        for(int i=0;i<sourceCount;i++){names[i]=Text(reader,1024);sourceUnits=checked(sourceUnits+names[i].Length);if(sourceUnits>MaxSourceUnits)throw new InvalidDataException("History source budget exceeded");}
                        int count=reader.ReadInt32();if(count<0 || count>MaxRecords)throw new InvalidDataException("Invalid history record count");
                        var entries=new List<Entry>(count);int units=0;
                        for(int i=0;i<count;i++) {
                            int sourceIndex=reader.ReadInt32();if(sourceIndex<0 || sourceIndex>=names.Length)throw new InvalidDataException("Invalid history source index");
                            var entry=new Entry {Source=names[sourceIndex]};byte idKind=reader.ReadByte();
                            if(idKind==1){var bytes=reader.ReadBytes(16);if(bytes.Length!=16)throw new EndOfStreamException();entry.Id=new Guid(bytes).ToString("N");}
                            else if(idKind==2)entry.Id=Text(reader,64);else if(idKind!=0)throw new InvalidDataException("Invalid history identity");
                            entry.Created=DateTime.FromBinary(reader.ReadInt64());entry.Updated=DateTime.FromBinary(reader.ReadInt64());
                            byte flags=reader.ReadByte();if((flags&~7)!=0)throw new InvalidDataException("Invalid history flags");
                            entry.PreviousVersion=(flags&1)!=0;entry.InputOnly=(flags&2)!=0;entry.Truncated=(flags&4)!=0;
                            entry.Text=Text(reader,MaxTextUnits);AddBudget(ref units,entry.Text);
                            int revisions=reader.ReadByte();if(revisions>8)throw new InvalidDataException("Invalid history revision count");
                            for(int v=0;v<revisions;v++){var version=new Revision {Saved=DateTime.FromBinary(reader.ReadInt64()),Text=Text(reader,MaxTextUnits)};AddBudget(ref units,version.Text);entry.Versions.Add(version);}
                            if(entry.HasContent)entries.Add(entry);
                        }
                        if(raw.Position!=raw.Length)throw new InvalidDataException("Unexpected history archive suffix");
                        return entries;
                    }
                }finally{Array.Clear(buffer,0,buffer.Length);Array.Clear(raw.GetBuffer(),0,(int)raw.Length);}
            }
        }
    }
}
