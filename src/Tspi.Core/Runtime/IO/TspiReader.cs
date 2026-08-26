using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using Tspi.Core.Math;

namespace Tspi.Core.IO
{
    /// <summary>
    /// Memory-mapped .tspi reader. Random access to any sample is O(1):
    /// index = (t - t0) / dt, byte = entity.DataOffset + index * stride.
    /// TrySampleAt provides playback-grade interpolation: cubic Hermite position
    /// (using stored velocities as tangents), Hermite-derivative velocity,
    /// slerped attitude, lerped body rates.
    /// </summary>
    public sealed class TspiReader : ITspiSource, IDisposable
    {
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _view;

        public string Path { get; private set; }
        public long FileLength { get; private set; }
        public TspiHeader Header { get; private set; }
        public TspiFooter Footer { get; private set; }
        public long FooterOffset { get; private set; }
        public long FooterLen { get; private set; }

        public IReadOnlyList<TspiEntityEntry> Entities => Footer.Entities;
        public IReadOnlyList<TspiEventEntry> Events => Footer.Events;
        public double DtSec => Header.DtSec;

        private TspiReader() { Path = ""; Header = new TspiHeader(); Footer = new TspiFooter(); }

        public static TspiReader Open(string path)
        {
            TspiFormat.RequireLittleEndian();
            var r = new TspiReader { Path = path };
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                r.FileLength = fs.Length;
                if (r.FileLength < TspiFormat.HeaderSize + TspiFormat.TrailerSize)
                    throw new InvalidDataException("File too small to be a .tspi: " + path);
                var headerBuf = new byte[TspiFormat.HeaderSize];
                if (!TspiFile.ReadExactly(fs, headerBuf, headerBuf.Length))
                    throw new InvalidDataException("Short read on header: " + path);
                r.Header = TspiHeader.ReadFrom(headerBuf);
                if (!TspiFile.TryReadTrailer(fs, r.FileLength - TspiFormat.TrailerSize, out long fOff, out long fLen))
                    throw new InvalidDataException(
                        "No valid trailer at EOF (torn write? run 'tspi recover'): " + path);
                r.FooterOffset = fOff;
                r.FooterLen = fLen;
                r.Footer = TspiFile.ReadFooterAt(fs, fOff, fLen);
            }
            r._mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            r._view = r._mmf.CreateViewAccessor(0, r.FileLength, MemoryMappedFileAccess.Read);
            r.ValidateEntityTable();
            return r;
        }

        private void ValidateEntityTable()
        {
            foreach (var e in Footer.Entities)
            {
                if (e.Layout != TspiFormat.LayoutSixDofV1)
                    continue; // unknown layouts are legal; callers must check before sampling
                if (e.Stride < TspiFormat.StrideSixDofV1)
                    throw new InvalidDataException("Entity '" + e.Id + "' stride below layout-1 prefix size");
                long end = e.DataOffset + e.SampleCount * e.Stride;
                if (e.DataOffset < TspiFormat.HeaderSize || end > FileLength)
                    throw new InvalidDataException("Entity '" + e.Id + "' block out of file bounds");
            }
        }

        public TspiEntityEntry FindEntity(string id)
        {
            foreach (var e in Footer.Entities)
                if (e.Id == id) return e;
            return null;
        }

        public double StartSec(TspiEntityEntry e) => e.T0Ns / 1e9;

        public double EndSec(TspiEntityEntry e) =>
            (e.T0Ns + (e.SampleCount - 1) * (long)Header.DtNs) / 1e9;

        public TspiRecord ReadSample(TspiEntityEntry e, long index)
        {
            if (index < 0 || index >= e.SampleCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            _view.Read(e.DataOffset + index * e.Stride, out TspiRecord rec);
            return rec;
        }

        /// <summary>
        /// Interpolated state at tSec (seconds since the header epoch). Returns false when
        /// t is outside the entity's alive window, unless clamp is true.
        /// </summary>
        public bool TrySampleAt(TspiEntityEntry e, double tSec, out TspiState state, bool clamp = false)
        {
            state = default;
            if (e.SampleCount <= 0 || e.Layout != TspiFormat.LayoutSixDofV1) return false;
            if (!TspiSampling.TryLocate(tSec, StartSec(e), EndSec(e), e.SampleCount, DtSec,
                    clamp, out long i, out double u))
                return false;
            if (e.SampleCount == 1)
            {
                state = TspiSampling.Single(ReadSample(e, 0));
                return true;
            }
            state = TspiSampling.Interpolate(ReadSample(e, i), ReadSample(e, i + 1), u, DtSec);
            return true;
        }

        /// <summary>A file is finished by definition; only a live stream keeps growing.</summary>
        public bool IsLive => false;

        /// <summary>Walk the footer chain, newest first (index 0 == current footer).</summary>
        public List<TspiFooter> ReadFooterChain()
        {
            var chain = new List<TspiFooter> { Footer };
            long? off = Footer.PrevFooterOffset, len = Footer.PrevFooterLen;
            using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int guard = 0;
                while (off.HasValue && len.HasValue && guard++ < 1024)
                {
                    var f = TspiFile.ReadFooterAt(fs, off.Value, len.Value);
                    chain.Add(f);
                    off = f.PrevFooterOffset;
                    len = f.PrevFooterLen;
                }
            }
            return chain;
        }

        public void Dispose()
        {
            _view?.Dispose();
            _mmf?.Dispose();
            _view = null;
            _mmf = null;
        }
    }
}
