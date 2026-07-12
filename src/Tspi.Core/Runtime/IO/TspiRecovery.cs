using System;
using System.IO;

namespace Tspi.Core.IO
{
    public sealed class TspiRecoveryReport
    {
        public bool TrailerValid;
        public long FileLength;
        /// <summary>Byte length of the newest recoverable state (0 if none found).</summary>
        public long RecoveredLength;
        public bool Truncated;
        public string Message = "";
    }

    /// <summary>
    /// Torn-write recovery. Appends never overwrite old bytes, so if the trailer at EOF
    /// is invalid the previous trailer is still intact somewhere before it: scan backward
    /// for the 8-byte trailer magic, validate footer adjacency + CRC, truncate after it.
    /// </summary>
    public static class TspiRecovery
    {
        private const int ChunkSize = 4 * 1024 * 1024;

        public static TspiRecoveryReport Inspect(string path) => Run(path, truncate: false);

        public static TspiRecoveryReport Recover(string path) => Run(path, truncate: true);

        private static TspiRecoveryReport Run(string path, bool truncate)
        {
            var report = new TspiRecoveryReport();
            using (var fs = new FileStream(path, FileMode.Open, truncate ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read))
            {
                report.FileLength = fs.Length;
                long eofTrailerStart = fs.Length - TspiFormat.TrailerSize;
                if (TspiFile.TryReadTrailer(fs, eofTrailerStart, out _, out _))
                {
                    report.TrailerValid = true;
                    report.RecoveredLength = fs.Length;
                    report.Message = "File is healthy; trailer at EOF is valid.";
                    return report;
                }

                long found = ScanBackwardForTrailer(fs, eofTrailerStart);
                if (found < 0)
                {
                    report.Message = "No valid trailer found anywhere; file is not recoverable as .tspi.";
                    return report;
                }
                report.RecoveredLength = found + TspiFormat.TrailerSize;
                if (truncate)
                {
                    fs.SetLength(report.RecoveredLength);
                    fs.Flush(true);
                    report.Truncated = true;
                    report.Message = "Truncated " + (report.FileLength - report.RecoveredLength) +
                                     " torn bytes; recovered to previous valid state.";
                }
                else
                {
                    report.Message = "Trailer at EOF invalid; a valid prior state ends at byte " +
                                     report.RecoveredLength + " (" +
                                     (report.FileLength - report.RecoveredLength) + " torn bytes).";
                }
                return report;
            }
        }

        /// <summary>Find the start offset of the newest valid trailer strictly before limit; -1 if none.</summary>
        private static long ScanBackwardForTrailer(FileStream fs, long limit)
        {
            byte[] magic = TspiFormat.TrailerMagic;
            long searchEnd = limit + magic.Length; // allow magic overlapping the invalid EOF trailer region
            if (searchEnd > fs.Length) searchEnd = fs.Length;
            var buf = new byte[ChunkSize + magic.Length - 1];
            long chunkStart = System.Math.Max(TspiFormat.HeaderSize, searchEnd - ChunkSize);
            while (true)
            {
                int want = (int)System.Math.Min(buf.Length, searchEnd - chunkStart);
                fs.Seek(chunkStart, SeekOrigin.Begin);
                if (!TspiFile.ReadExactly(fs, buf, want)) return -1;
                for (int i = want - magic.Length; i >= 0; i--)
                {
                    bool match = true;
                    for (int k = 0; k < magic.Length; k++)
                        if (buf[i + k] != magic[k]) { match = false; break; }
                    if (!match) continue;
                    long magicAbs = chunkStart + i;
                    long trailerStart = magicAbs - 24; // magic is the last 8 bytes of the 32-byte trailer
                    if (trailerStart < TspiFormat.HeaderSize) continue;
                    if (trailerStart >= limit) continue; // that's the already-invalid EOF trailer
                    if (TspiFile.TryReadTrailer(fs, trailerStart, out _, out _))
                        return trailerStart;
                    fs.Seek(chunkStart, SeekOrigin.Begin);
                    if (!TspiFile.ReadExactly(fs, buf, want)) return -1;
                }
                if (chunkStart <= TspiFormat.HeaderSize) return -1;
                long newStart = System.Math.Max(TspiFormat.HeaderSize, chunkStart - ChunkSize);
                searchEnd = chunkStart + magic.Length - 1; // overlap so boundary-straddling magic is seen
                chunkStart = newStart;
            }
        }
    }
}
