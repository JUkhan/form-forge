namespace FormForge.Api.Features.Designer.Dtos;

// Body for PUT /api/designers/{designerId}/versions/{version}/dataset.
// A null DatasetId clears the dataset binding for the version; a non-null value
// must reference an existing CustomDataset. When set, the record-list endpoint
// reads rows from that dataset's backing VIEW instead of the provisioned table.
internal sealed record SetDatasetRequest(System.Guid? DatasetId);
