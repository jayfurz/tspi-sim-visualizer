using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Tspi.Core.IO
{
    /// <summary>One entity's samples staged for writing.</summary>
    public sealed class TspiEntityBlock
    {
        /// <summary>Caller fills identity fields (ord/id/team/type/model/parent/t0).
        /// The writer fills DataOffset/SampleCount/Stride/Layout.</summary>
        public TspiEntityEntry Meta = new TspiEntityEntry();
        public List<TspiRecord> Records = new List<TspiRecord>();
    }

    /// <summary>
    /// .tspi writer. Two operations:
    ///   WriteNew  - header + blocks + footer + trailer.
    ///   Append    - new blocks + merged footer + trailer at EOF; never touches old bytes,
    ///               so a torn append is recoverable and concurrent readers are unaffected.
    /// </summary>
    public static class TspiFile
    {
        /// <summary>
        /// Buffer whole blocks then write. Convenience for callers (mostly tests) that
        /// already hold full record lists; the sim uses <see cref="TspiStreamWriter"/> to
        /// stream records without a second full copy in memory.
        /// </summary>
        public static void WriteNew(string path, TspiHeader header, IReadOnlyList<TspiEntityBlock> blocks,
            IReadOnlyList<TspiEventEntry> events, IReadOnlyList<Dictionary<string, object>> provenance,
            Dictionary<string, object> environment = null)
        {
            RequireUniqueOrds(blocks.Select(b => b.Meta.Ord));
            using (var w = new TspiStreamWriter(path, header))
            {
                foreach (var block in blocks)
                    w.WriteBlock(block.Meta, block.Records, block.Records.Count);
                if (events != null) w.AddEvents(events);
                if (provenance != null) foreach (var p in provenance) w.AddProvenance(p);
                if (environment != null) w.SetEnvironment(environment);
                w.Finish();
            }
        }

        public static void Append(string path, IReadOnlyList<TspiEntityBlock> blocks,
            IReadOnlyList<TspiEventEntry> newEvents, Dictionary<string, object> provenanceEntry)
        {
            TspiFormat.RequireLittleEndian();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                if (!TryReadTrailer(fs, fs.Length - TspiFormat.TrailerSize, out long footerOffset, out long footerLen))
                    throw new InvalidDataException("File has no valid trailer at EOF (run recovery first): " + path);
                TspiFooter footer = ReadFooterAt(fs, footerOffset, footerLen);
                var existingOrds = new HashSet<uint>(footer.Entities.Select(e => e.Ord));
                foreach (var b in blocks)
                    if (!existingOrds.Add(b.Meta.Ord))
                        throw new InvalidOperationException("Appended entity ord " + b.Meta.Ord + " already exists in file");

                fs.Seek(0, SeekOrigin.End);
                foreach (var block in blocks)
                    WriteBlockStreaming(fs, block.Meta, block.Records, block.Records.Count);

                footer.Entities.AddRange(blocks.Select(b => b.Meta));
                if (newEvents != null) footer.Events.AddRange(newEvents);
                footer.Events = footer.Events.OrderBy(e => e.TNs).ToList();
                if (provenanceEntry != null) footer.Provenance.Add(provenanceEntry);
                footer.PrevFooterOffset = footerOffset;
                footer.PrevFooterLen = footerLen;
                WriteFooterAndTrailer(fs, footer);
                fs.Flush(true);
            }
        }

        // ---------------- shared low-level pieces (also used by reader/recovery) ----------------

        private static void RequireUniqueOrds(IEnumerable<uint> ords)
        {
            var seen = new HashSet<uint>();
            foreach (var o in ords)
                if (!seen.Add(o))
                    throw new InvalidOperationException("Duplicate entity ord " + o);
        }

        /// <summary>
        /// Write one entity block, streaming records through a fixed chunk buffer so a
        /// full second copy of the trajectory is never materialized. <paramref name="count"/>
        /// is authoritative (trajectories know their length up front), so no count patching
        /// is needed and the bytes are identical to a buffered write.
        /// </summary>
        internal static void WriteBlockStreaming(Stream fs, TspiEntityEntry meta,
            IEnumerable<TspiRecord> records, long count)
        {
            meta.Stride = TspiFormat.StrideSixDofV1;
            meta.Layout = TspiFormat.LayoutSixDofV1;
            meta.SampleCount = count;

            var w = new BinaryWriter(fs);
            w.Write(TspiFormat.BlockMagic);
            w.Write(meta.Ord);
            w.Write((ushort)meta.Layout);
            w.Write((ushort)meta.Stride);
            w.Write(0u); // reserved
            w.Write(meta.T0Ns);
            w.Write((ulong)count);
            w.Flush();

            meta.DataOffset = fs.Position;

            const int chunk = 8192;
            var buf = new TspiRecord[chunk];
            int n = 0;
            long written = 0;
            foreach (var rec in records)
            {
                buf[n++] = rec;
                if (n == chunk)
                {
                    fs.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<TspiRecord>(buf, 0, n)));
                    written += n;
                    n = 0;
                }
            }
            if (n > 0)
            {
                fs.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<TspiRecord>(buf, 0, n)));
                written += n;
            }
            if (written != count)
                throw new InvalidOperationException(
                    $"block '{meta.Id}' declared {count} samples but streamed {written}");
        }

        internal static void WriteFooterAndTrailer(Stream fs, TspiFooter footer)
        {
            byte[] json = Encoding.UTF8.GetBytes(footer.ToJsonString());
            long footerOffset = fs.Position;
            fs.Write(json, 0, json.Length);
            var w = new BinaryWriter(fs);
            w.Write((ulong)footerOffset);
            w.Write((ulong)json.Length);
            w.Write(Crc32.Compute(json, 0, json.Length));
            w.Write(0u); // reserved
            w.Write(TspiFormat.TrailerMagic);
            w.Flush();
        }

        /// <summary>
        /// Validate and read the trailer whose first byte is at trailerStart.
        /// Checks magic, bounds, footer adjacency (footer must end exactly at the trailer), and footer CRC.
        /// </summary>
        internal static bool TryReadTrailer(Stream fs, long trailerStart, out long footerOffset, out long footerLen)
        {
            footerOffset = 0;
            footerLen = 0;
            if (trailerStart < TspiFormat.HeaderSize) return false;
            if (trailerStart + TspiFormat.TrailerSize > fs.Length) return false;
            var buf = new byte[TspiFormat.TrailerSize];
            fs.Seek(trailerStart, SeekOrigin.Begin);
            if (!ReadExactly(fs, buf, buf.Length)) return false;
            for (int i = 0; i < 8; i++)
                if (buf[24 + i] != TspiFormat.TrailerMagic[i]) return false;
            long off = checked((long)BitConverter.ToUInt64(buf, 0));
            long len = checked((long)BitConverter.ToUInt64(buf, 8));
            uint crc = BitConverter.ToUInt32(buf, 16);
            if (len <= 0 || len > TspiFormat.MaxFooterBytes) return false;
            if (off < TspiFormat.HeaderSize) return false;
            if (off + len != trailerStart) return false; // footer must sit immediately before its trailer
            var json = new byte[len];
            fs.Seek(off, SeekOrigin.Begin);
            if (!ReadExactly(fs, json, (int)len)) return false;
            if (Crc32.Compute(json, 0, json.Length) != crc) return false;
            footerOffset = off;
            footerLen = len;
            return true;
        }

        internal static TspiFooter ReadFooterAt(Stream fs, long offset, long len)
        {
            var json = new byte[len];
            fs.Seek(offset, SeekOrigin.Begin);
            if (!ReadExactly(fs, json, (int)len))
                throw new InvalidDataException("Short read while loading footer");
            return TspiFooter.FromJsonString(Encoding.UTF8.GetString(json));
        }

        internal static bool ReadExactly(Stream fs, byte[] buf, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = fs.Read(buf, read, count - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }
    }
}
