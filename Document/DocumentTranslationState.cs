namespace ScreenTranslator.Document;

public enum DocumentTranslationState
{
    Empty, Importing, Analyzing, Ready, Translating, Cancelling, Cancelled,
    Completed, Exporting, Exported, Faulted
}

public static class DocumentStateTransitions
{
    public static bool CanMoveTo(this DocumentTranslationState from, DocumentTranslationState to) =>
        to == DocumentTranslationState.Faulted || (from, to) switch
        {
            (DocumentTranslationState.Empty, DocumentTranslationState.Importing) => true,
            (DocumentTranslationState.Importing, DocumentTranslationState.Analyzing) => true,
            (DocumentTranslationState.Analyzing, DocumentTranslationState.Ready) => true,
            (DocumentTranslationState.Ready, DocumentTranslationState.Translating) => true,
            (DocumentTranslationState.Cancelled, DocumentTranslationState.Translating) => true,
            (DocumentTranslationState.Completed, DocumentTranslationState.Translating) => true,
            (DocumentTranslationState.Translating, DocumentTranslationState.Cancelling) => true,
            (DocumentTranslationState.Cancelling, DocumentTranslationState.Cancelled) => true,
            (DocumentTranslationState.Translating, DocumentTranslationState.Completed) => true,
            (DocumentTranslationState.Completed, DocumentTranslationState.Exporting) => true,
            (DocumentTranslationState.Exported, DocumentTranslationState.Exporting) => true,
            (DocumentTranslationState.Exporting, DocumentTranslationState.Exported) => true,
            (_, DocumentTranslationState.Empty) => true,
            (DocumentTranslationState.Faulted, DocumentTranslationState.Importing) => true,
            _ => false
        };
}

