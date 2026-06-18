namespace Backend.Core.Storage;

/// <summary>
/// A read object: its bytes and the content type to serve them with. Returned by
/// <see cref="IStorageService.GetAsync"/> so the API can proxy a stored asset to the browser as a
/// renderable response, brand-scoped through the run that owns the key.
/// </summary>
public sealed record StorageObject(byte[] Content, string ContentType);
