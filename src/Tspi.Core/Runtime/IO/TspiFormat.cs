using System;
using System.IO;

namespace Tspi.Core.IO
{
    /// <summary>
    /// Normative constants for the .tspi container (format version 1).
    /// Layout (all little-endian):
    ///   [header 128 B] [entity block]* [footer JSON] [trailer 32 B]  ... appends repeat blocks/footer/trailer.
    /// Each entity block: [block header 32 B][sampleCount * stride record bytes].
    /// The trailer at EOF locates the newest footer; appends never overwrite old bytes.
    /// See docs/FORMAT.md.
    /// </summary>
    public static class TspiFormat
    {
        public const uint Version = 1;
        public const int HeaderSize = 128;
        public const int BlockHeaderSize = 32;
        public const int TrailerSize = 32;

        /// <summary>Record layout 1: pos f64x3 | vel f32x3 | quat wxyz f32x4 | omega_body f32x3 = 64 bytes.</summary>
        public const int LayoutSixDofV1 = 1;
        public const int StrideSixDofV1 = 64;

        public static readonly byte[] FileMagic = { (byte)'T', (byte)'S', (byte)'P', (byte)'I' };
        public static readonly byte[] BlockMagic = { (byte)'E', (byte)'B', (byte)'L', (byte)'K' };
        public static readonly byte[] TrailerMagic =
            { (byte)'T', (byte)'S', (byte)'P', (byte)'I', (byte)'F', (byte)'T', (byte)'R', (byte)'1' };

        /// <summary>Sanity bound when reading untrusted footer lengths.</summary>
        public const long MaxFooterBytes = 1L << 28;

        public static void RequireLittleEndian()
        {
            if (!BitConverter.IsLittleEndian)
                throw new PlatformNotSupportedException(".tspi I/O requires a little-endian host");
        }
    }

    /// <summary>Fixed 128-byte file header. Written once at create time; never modified by appends.</summary>
    public sealed class TspiHeader
    {
        public uint Version = TspiFormat.Version;
        public uint Flags;
        /// <summary>Fixed sample period in nanoseconds (shared by every entity block in the file).</summary>
        public ulong DtNs;
        /// <summary>Absolute UTC epoch of sim time zero, in Unix nanoseconds. All in-file times are relative to this.</summary>
        public long EpochUnixNs;
        public double OriginLatDeg;
        public double OriginLonDeg;
        public double OriginAltM;
        /// <summary>SHA-256 of the scenario manifest that produced the initial write (32 bytes).</summary>
        public byte[] ManifestSha256 = new byte[32];

        public double DtSec => DtNs / 1e9;

        public void WriteTo(Stream stream)
        {
            TspiFormat.RequireLittleEndian();
            if (ManifestSha256 == null || ManifestSha256.Length != 32)
                throw new InvalidOperationException("ManifestSha256 must be exactly 32 bytes");
            var w = new BinaryWriter(stream);
            long start = stream.Position;
            w.Write(TspiFormat.FileMagic);
            w.Write(Version);
            w.Write(Flags);
            w.Write(0u); // reserved
            w.Write(DtNs);
            w.Write(EpochUnixNs);
            w.Write(OriginLatDeg);
            w.Write(OriginLonDeg);
            w.Write(OriginAltM);
            w.Write(ManifestSha256);
            long written = stream.Position - start;
            for (long i = written; i < TspiFormat.HeaderSize; i++) w.Write((byte)0);
            w.Flush();
        }

        public static TspiHeader ReadFrom(byte[] buf)
        {
            if (buf.Length < TspiFormat.HeaderSize)
                throw new FormatException("Header buffer too small");
            for (int i = 0; i < 4; i++)
                if (buf[i] != TspiFormat.FileMagic[i])
                    throw new FormatException("Bad .tspi file magic");
            var h = new TspiHeader
            {
                Version = BitConverter.ToUInt32(buf, 4),
                Flags = BitConverter.ToUInt32(buf, 8),
                DtNs = BitConverter.ToUInt64(buf, 16),
                EpochUnixNs = BitConverter.ToInt64(buf, 24),
                OriginLatDeg = BitConverter.ToDouble(buf, 32),
                OriginLonDeg = BitConverter.ToDouble(buf, 40),
                OriginAltM = BitConverter.ToDouble(buf, 48),
            };
            Array.Copy(buf, 56, h.ManifestSha256, 0, 32);
            if (h.Version != TspiFormat.Version)
                throw new FormatException("Unsupported .tspi format version " + h.Version);
            if (h.DtNs == 0)
                throw new FormatException("Header dt_ns must be nonzero");
            return h;
        }
    }
}
