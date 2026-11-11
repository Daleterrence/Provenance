namespace Provenance.Common.Game.ThirdParty;

public enum AddonDocsKind { Link, Page, Text }

public sealed record AddonDocs(AddonDocsKind Kind, string Target);
