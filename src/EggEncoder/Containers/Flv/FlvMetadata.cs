using EggEncoder.Live.Rtmp.Amf0;
using EggEncoder.Media;

namespace EggEncoder.Containers.Flv
{
    /// <summary>
    /// An FLV <c>onMetaData</c> script tag (FLV specification v10.1 Annex E.5): every property as read,
    /// in wire order, plus typed accessors for the common ones. Purely descriptive: nothing here drives
    /// demuxing, which reads codec configuration and timing from the audio/video tags themselves.
    /// </summary>
    public sealed class FlvMetadata
    {
        /// <summary>Wraps these properties (copied, in order).</summary>
        public FlvMetadata(IEnumerable<KeyValuePair<string, Amf0Value>> properties)
        {
            ArgumentNullException.ThrowIfNull(properties);
            _properties = Amf0Value.FromEcmaArray(properties);
        }

        private readonly Amf0Value _properties;

        /// <summary>Every property, in wire order.</summary>
        public IReadOnlyList<KeyValuePair<string, Amf0Value>> Properties => _properties.Properties;

        /// <summary><c>duration</c> in seconds; <c>null</c> when absent or not a number. Live writers write 0.</summary>
        public double? Duration => GetNumber("duration");

        /// <summary><c>width</c> in pixels.</summary>
        public double? Width => GetNumber("width");

        /// <summary><c>height</c> in pixels.</summary>
        public double? Height => GetNumber("height");

        /// <summary><c>framerate</c> in frames per second.</summary>
        public double? FrameRate => GetNumber("framerate");

        /// <summary><c>videodatarate</c> in kilobits per second.</summary>
        public double? VideoDataRate => GetNumber("videodatarate");

        /// <summary><c>audiodatarate</c> in kilobits per second.</summary>
        public double? AudioDataRate => GetNumber("audiodatarate");

        /// <summary><c>audiosamplerate</c> in Hz.</summary>
        public double? AudioSampleRate => GetNumber("audiosamplerate");

        /// <summary><c>encoder</c>: the software that wrote the stream.</summary>
        public string? Encoder => Find("encoder") is { Type: Amf0Type.String or Amf0Type.LongString } value ? value.String : null;

        /// <summary>
        /// The metadata <see cref="FlvWriter"/> writes when none is given: the width/height of the video
        /// track and the codec, sample rate and channel layout of each track, plus <c>encoder</c>.
        /// </summary>
        public static FlvMetadata FromTracks(MediaTrackSet tracks, string? encoder = "EggEncoder")
        {
            ArgumentNullException.ThrowIfNull(tracks);
            var properties = new List<KeyValuePair<string, Amf0Value>> { Number("duration", 0) };
            foreach (var track in tracks.Tracks)
            {
                if (track.Kind == MediaKind.Video)
                {
                    if (track.Width > 0 && track.Height > 0)
                    {
                        properties.Add(Number("width", track.Width));
                        properties.Add(Number("height", track.Height));
                    }

                    properties.Add(Number("videocodecid", FlvWriter.GetVideoCodecId(track.Codec)));
                }
                else if (track.Kind == MediaKind.Audio)
                {
                    if (track.SampleRate > 0)
                    {
                        properties.Add(Number("audiosamplerate", track.SampleRate));
                    }

                    if (track.Channels > 0)
                    {
                        properties.Add(new("stereo", Amf0Value.FromBoolean(track.Channels > 1)));
                    }

                    properties.Add(Number("audiocodecid", FlvWriter.GetSoundFormat(track.Codec)));
                }
            }

            if (encoder is not null)
            {
                properties.Add(new("encoder", Amf0Value.FromString(encoder)));
            }

            return new FlvMetadata(properties);
        }

        /// <summary>The value of the first property with this name, or <c>null</c>.</summary>
        public Amf0Value? Find(string name) => _properties.GetProperty(name);

        private double? GetNumber(string name) => Find(name) is { Type: Amf0Type.Number } value ? value.Number : null;

        private static KeyValuePair<string, Amf0Value> Number(string name, double value) => new(name, Amf0Value.FromNumber(value));
    }
}
