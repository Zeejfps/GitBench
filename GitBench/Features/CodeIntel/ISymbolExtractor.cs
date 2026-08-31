namespace GitBench.Features.CodeIntel;

internal interface ISymbolExtractor
{
    CodeIntelAvailability Availability { get; }

    FileOutline? Extract(string text, CodeLanguage language);
}
