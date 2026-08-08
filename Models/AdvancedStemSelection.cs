namespace DrumPracticeStudio.Models;

[Flags]
public enum AdvancedStemSelection
{
    None = 0,
    Drums = 1,
    Bass = 2,
    LeadVocal = 4,
    BackVocal = 8,
    LeadGuitar = 16,
    RhythmGuitar = 32,
    Piano = 64,
    Other = 128,
    All = Drums | Bass | LeadVocal | BackVocal | LeadGuitar | RhythmGuitar | Piano | Other
}
