namespace EggEncoder.Containers.Flv
{
    /// <summary>The FLV tag types (FLV specification v10.1, Annex E.4.1).</summary>
    public enum FlvTagType : byte
    {
        Audio = 8,
        Video = 9,
        /// <summary>Script data: AMF0 values such as <c>onMetaData</c>.</summary>
        ScriptData = 18
    }
}
