namespace EggEncoder.Codecs.Tta
{
    // TTA's order-8 adaptive filter, the second decode stage (after Rice+zigzag, before the fixed
    // predictor). Confirmed bit-exact against ffmpeg's ttadsp.c (tta_filter_process_c, decode) and
    // ttaencdsp.c (ttaenc_filter_process_c, encode) -- genuinely two different functions, not a single
    // function with a direction flag, since decode and encode differ in exactly where the dl[] history
    // update reads its input from and exactly when `error` gets captured (decode: before adding the
    // filter correction, i.e. the pre-filter residual; encode: after subtracting it, i.e. the
    // post-filter residual) -- these turn out to be the same number on both sides by construction
    // (the quantity that crosses the Rice coder boundary), so state stays in lock-step, but trying to
    // unify the two into one shared method risks getting that one-line difference backwards, so this
    // mirrors ffmpeg's own two-function structure instead of forcing a shared implementation the way
    // AlacLpcPredictor could for ALAC.
    //
    // Resets fresh (every field zeroed) at the start of every frame, same as ALAC's per-frame state.
    internal sealed class TtaAdaptiveFilter
    {
        private const int Order = 8;

        private readonly int[] _qm = new int[Order];
        private readonly int[] _dx = new int[Order];
        private readonly int[] _dl = new int[Order];
        private readonly int _shift;
        private readonly int _round;
        private int _error;

        public TtaAdaptiveFilter(int shift)
        {
            _shift = shift;
            _round = 1 << (shift - 1);
        }

        public int Decode(int residual)
        {
            var sum = Adapt();

            _error = residual;
            var output = residual + (sum >> _shift);

            UpdateHistory(output);

            return output;
        }

        public int Encode(int sample)
        {
            var sum = Adapt();

            UpdateHistory(sample);

            var residual = sample - (sum >> _shift);
            _error = residual;

            return residual;
        }

        // Sign-sign-adapts qm[] from the previous call's error, then shifts dx/dl[0..3] down one slot
        // and recomputes dx[4..7] from the (still old, not yet updated this call) dl[4..7] -- shared
        // between decode/encode since this part really is identical both directions. Returns the
        // round-plus-dot-product sum the caller still needs to combine with its own residual/sample.
        private int Adapt()
        {
            if (_error < 0)
            {
                for (var i = 0; i < Order; i++)
                {
                    _qm[i] -= _dx[i];
                }
            }
            else if (_error > 0)
            {
                for (var i = 0; i < Order; i++)
                {
                    _qm[i] += _dx[i];
                }
            }

            var sum = _round;
            for (var i = 0; i < Order; i++)
            {
                sum += _dl[i] * _qm[i];
            }

            _dx[0] = _dx[1];
            _dx[1] = _dx[2];
            _dx[2] = _dx[3];
            _dx[3] = _dx[4];
            _dl[0] = _dl[1];
            _dl[1] = _dl[2];
            _dl[2] = _dl[3];
            _dl[3] = _dl[4];

            _dx[4] = (_dl[4] >> 30) | 1;
            _dx[5] = ((_dl[5] >> 30) | 2) & ~1;
            _dx[6] = ((_dl[6] >> 30) | 2) & ~1;
            _dx[7] = ((_dl[7] >> 30) | 4) & ~3;

            return sum;
        }

        private void UpdateHistory(int value)
        {
            _dl[4] = -_dl[5];
            _dl[5] = -_dl[6];
            _dl[6] = value - _dl[7];
            _dl[7] = value;
            _dl[5] += _dl[6];
            _dl[4] += _dl[5];
        }
    }
}
