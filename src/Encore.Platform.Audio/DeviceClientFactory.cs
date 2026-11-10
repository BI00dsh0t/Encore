namespace Encore.Platform.Audio;

/// <summary>Opens a client; invalidated is called at most once, from the client's thread, and must only set a flag.</summary>
internal delegate IDeviceClient DeviceClientFactory(string endpointId, AudioFlow flow, Action invalidated);
