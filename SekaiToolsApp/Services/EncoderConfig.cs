namespace SekaiToolsApp.Services;

public enum VideoEncoder
{
    Libx264,
    Libx265,
    LibSvtAv1,
    H264VideoToolbox,
    HevcVideoToolbox,
    H264Nvenc,
    HevcNvenc,
    Av1Nvenc,
    H264Qsv,
    HevcQsv,
    Av1Qsv,
    H264Amf,
    HevcAmf,
    Av1Amf,
}

public static class VideoEncoderExtensions
{
    public static string DisplayName(this VideoEncoder encoder) => encoder switch
    {
        VideoEncoder.Libx264 => "x264",
        VideoEncoder.Libx265 => "x265",
        VideoEncoder.LibSvtAv1 => "SVT-AV1",
        VideoEncoder.H264VideoToolbox => "H264 VideoToolbox",
        VideoEncoder.HevcVideoToolbox => "HEVC VideoToolbox",
        VideoEncoder.H264Nvenc => "H264 NVENC",
        VideoEncoder.HevcNvenc => "HEVC NVENC",
        VideoEncoder.Av1Nvenc => "AV1 NVENC",
        VideoEncoder.H264Qsv => "H264 QSV",
        VideoEncoder.HevcQsv => "HEVC QSV",
        VideoEncoder.Av1Qsv => "AV1 QSV",
        VideoEncoder.H264Amf => "H264 AMF",
        VideoEncoder.HevcAmf => "HEVC AMF",
        VideoEncoder.Av1Amf => "AV1 AMF",
        _ => encoder.ToString(),
    };
}
