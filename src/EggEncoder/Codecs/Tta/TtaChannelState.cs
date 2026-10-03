using EggEncoder.Transform;

namespace EggEncoder.Codecs.Tta
{
    // Bundles one channel's three per-sample pipeline stages (Rice coder, adaptive filter, fixed
    // predictor), all of which reset fresh at the start of every frame -- construct a new instance per
    // channel per frame rather than reusing one across frames.
    internal sealed class TtaChannelState
    {
        private readonly TtaRiceCoder _rice = new();
        private readonly TtaAdaptiveFilter _filter;
        private readonly TtaFixedPredictor _predictor = new();

        public TtaChannelState(int filterShift)
        {
            _filter = new TtaAdaptiveFilter(filterShift);
        }

        public int Decode(BitReader reader)
        {
            var residual = _rice.DecodeResidual(reader);
            var filtered = _filter.Decode(residual);
            return _predictor.Decode(filtered);
        }

        public void Encode(BitWriter writer, int sample)
        {
            var predicted = _predictor.Encode(sample);
            var filterResidual = _filter.Encode(predicted);
            _rice.EncodeResidual(writer, filterResidual);
        }
    }
}
