namespace EggEncoder
{
    /// <summary>
    /// Settings for <see cref="NativeEncoder"/>, passed to its constructor or to
    /// <see cref="ServiceCollectionExtensions.AddEggEncoder(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{EggEncoderOptions})"/>.
    /// </summary>
    public sealed class EggEncoderOptions
    {
        private int? _maxDegreeOfParallelism;

        /// <summary>Whether <see cref="NativeEncoder"/> logs the start, completion and failure of each call. Defaults to true.</summary>
        public bool EnableLogging { get; set; } = true;

        /// <summary>
        /// The most threads one <see cref="NativeEncoder"/> call uses at once for MP3 encoding and decoding and for
        /// decoding the inputs of a mix. Null keeps the library default (the processor count, at most 4 for MP3).
        /// The output is the same for every value; a lower value leaves cores free for other work.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
        public int? MaxDegreeOfParallelism
        {
            get => _maxDegreeOfParallelism;
            set
            {
                if (value is < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "MaxDegreeOfParallelism must be at least 1, or null for the default");
                }

                _maxDegreeOfParallelism = value;
            }
        }
    }
}
