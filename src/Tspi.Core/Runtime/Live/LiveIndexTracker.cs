using Tspi.Core.IO;

namespace Tspi.Core.Live
{
    /// <summary>
    /// The bookkeeping every consumer of a live stream must get identically right when a
    /// record arrives: is this a duplicate, a late join, or a hole left by a dropped
    /// frame? (tools/live-stream/PROTOCOL.md, "Consumer rules".)
    ///
    /// It lives here, in one struct, because the rules are subtle and there is more than
    /// one consumer — <see cref="LiveTspiSource"/> (viewers, in memory) and the
    /// <c>LiveRecorder</c> sink (spooled to disk). They must not drift; the JS viewer's
    /// <c>LiveTspiFile</c> ports the same decisions.
    /// </summary>
    public struct LiveIndexTracker
    {
        /// <summary>Samples stored locally so far.</summary>
        public long Count;
        /// <summary>Producer-side index that local sample 0 corresponds to (set by a late join).</summary>
        public long IndexOffset;

        /// <summary>
        /// Decide what to do with a record the producer labelled <paramref name="wireIndex"/>.
        ///
        /// Returns false for a duplicate or stale index (the first write wins). Returns
        /// true to store it, with <paramref name="padCount"/> samples of the previous
        /// record to repeat first so that t = t0 + i*dt stays exact across a dropped
        /// frame. When this is the entity's first record, storage is rebased onto it and
        /// <paramref name="t0Ns"/> is advanced to that record's true time, so joining a
        /// run in progress starts the trail at the join point without lying about when
        /// the samples happened.
        ///
        /// The caller stores the padding and the record, then calls <see cref="Stored"/>.
        /// </summary>
        public bool Accept(uint wireIndex, long dtNs, ref long t0Ns, out long padCount)
        {
            padCount = 0;
            long index = (long)wireIndex - IndexOffset;
            if (index < Count) return false;
            if (index > Count)
            {
                if (Count == 0)
                {
                    IndexOffset += index;
                    t0Ns += index * dtNs;
                }
                else
                {
                    padCount = index - Count;
                }
            }
            return true;
        }

        /// <summary>Advance the local count after storing <paramref name="padCount"/> fills plus one record.</summary>
        public void Stored(long padCount) => Count += padCount + 1;
    }
}
