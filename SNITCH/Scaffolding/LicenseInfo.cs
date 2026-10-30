namespace Snitch.Scaffolding;

/// <summary>One row of <c>licenses.index.tsv</c>, exposed by <c>list_licenses</c>.</summary>
public sealed record LicenseInfo(
    string Id,
    string Name,
    string Spdx,
    string Family,
    bool OsiApproved,
    bool PatentGrant);
