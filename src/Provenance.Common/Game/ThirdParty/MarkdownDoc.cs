namespace Provenance.Common.Game.ThirdParty;

public sealed record MarkdownDoc(IReadOnlyList<MdBlock> Blocks);

public abstract record MdBlock;

public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Text) : MdBlock;

public sealed record MdParagraph(IReadOnlyList<MdInline> Text) : MdBlock;

public sealed record MdCode(string Text) : MdBlock;

public sealed record MdList(int? Start, IReadOnlyList<IReadOnlyList<MdBlock>> Items) : MdBlock;

public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;

public sealed record MdTable(IReadOnlyList<IReadOnlyList<MdInline>> Header, IReadOnlyList<IReadOnlyList<IReadOnlyList<MdInline>>> Rows) : MdBlock;

public sealed record MdRule : MdBlock;

public abstract record MdInline;

public sealed record MdText(string Text) : MdInline;

public sealed record MdCodeSpan(string Text) : MdInline;

public sealed record MdStyle(bool Bold, IReadOnlyList<MdInline> Inner) : MdInline;

public sealed record MdLink(IReadOnlyList<MdInline> Inner, Uri Web) : MdInline;

public sealed record MdBreak : MdInline;
