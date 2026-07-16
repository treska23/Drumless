namespace DrumPracticeStudio.Models;

[Flags]
public enum StemSelection
{
    None = 0,
    Drums = 1,
    Bass = 2,
    Vocals = 4,
    Other = 8,
    Guitar = 16,
    Piano = 32,
    Drumless = Bass | Vocals | Guitar | Piano | Other,
    All = Drums | Bass | Vocals | Guitar | Piano | Other
}
