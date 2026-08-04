using EggEncoder.Results;

namespace EggEncoder
{
    public interface IMediaEncoder
    {
        Task<ProbeResult> Probe(string filePath);

        Task ConvertFile(string sourceFilePath, string destFilePath);

        Task CutFile(string sourceFilePath, string destFilePath, int startInSeconds, int endInSeconds);
    }
}
