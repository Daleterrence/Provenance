namespace Provenance.Common.Game.ThirdParty;

public enum AddonDocsKind { Link, Page, Text }

// Link is a web address, Page an .html file on disk, Text a text file on disk
public sealed record AddonDocs(AddonDocsKind Kind, string Target);
