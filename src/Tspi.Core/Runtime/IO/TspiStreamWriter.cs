using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Tspi.Core.IO
{
    /// <summary>
    /// Incremental .tspi writer: header first, then one entity block at a time streamed
    /// straight to disk, then a single footer + trailer at Finish(). Peak memory is one
    /// block's chunk buffer rather than the whole file, so a producer can integrate an
    /// entity, write it, and drop it. Produces byte-identical output to
    /// <see cref="TspiFile.WriteNew"/>.
    ///
    /// If the process dies before Finish(), the file has no valid trailer and is a torn
    /// write — recoverable to the last complete state (which, for a fresh file, is none).
    /// </summary>
    public sealed class TspiStreamWriter : IDisposable
    {
        private readonly FileStream _fs;
        private readonly TspiFooter _footer = new TspiFooter();
        private readonly HashSet<uint> _ords = new HashSet<uint>();
        private bool _finished;

        public TspiStreamWriter(string path, TspiHeader header)
        {
            TspiFormat.RequireLittleEndian();
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            header.WriteTo(_fs);
        }

        /// <summary>Stream one entity's block. <paramref name="count"/> must equal the number of records enumerated.</summary>
        public void WriteBlock(TspiEntityEntry meta, IEnumerable<TspiRecord> records, long count)
        {
            if (_finished) throw new InvalidOperationException("writer already finished");
            if (!_ords.Add(meta.Ord))
                throw new InvalidOperationException("Duplicate entity ord " + meta.Ord);
            TspiFile.WriteBlockStreaming(_fs, meta, records, count);
            _footer.Entities.Add(meta);
        }

        public void AddEvents(IEnumerable<TspiEventEntry> events)
        {
            if (events != null) _footer.Events.AddRange(events);
        }

        public void AddProvenance(Dictionary<string, object> record)
        {
            if (record != null) _footer.Provenance.Add(record);
        }

        public void SetEnvironment(Dictionary<string, object> environment)
        {
            _footer.Environment = environment;
        }

        /// <summary>Order events by time, write footer + trailer, and fsync.</summary>
        public void Finish()
        {
            if (_finished) return;
            _footer.Events = _footer.Events.OrderBy(e => e.TNs).ToList();
            TspiFile.WriteFooterAndTrailer(_fs, _footer);
            _fs.Flush(true);
            _finished = true;
        }

        public void Dispose()
        {
            _fs?.Dispose();
        }
    }
}
