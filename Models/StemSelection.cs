namespace DrumPracticeStudio.Models;

[Flags]
public enum StemSelection
{
    None = 0,
    Drums = 1,
    Bass = 2,
    Vocals = 4,
    Other = 8,
    Drumless = Bass | Vocals | Other,
    All = Drums | Bass | Vocals | Other
}
