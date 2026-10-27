namespace PrintLogApi.Authentication.Devices;

/// <summary>
/// Lets a device token (#148) reach this action. Everything without it refuses a device token
/// with 403 before the action runs (<see cref="DeviceTokenMiddleware"/>), so the set of endpoints
/// a paired device can use is exactly the actions carrying this attribute.
/// <c>DeviceAccessCoverageTests</c> pins that set; adding the attribute anywhere is a deliberate
/// widening and fails that test until the list there is updated.
///
/// Method-only on purpose: on a controller it would open every action the controller grows later.
/// The action must authorize through the default policy (a bare <c>[Authorize]</c>, or anonymous),
/// which accepts the principal the middleware sets; a scheme-pinned policy would drop it.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class DeviceAccessAttribute(string? scope = null) : Attribute
{
    /// <summary>The <see cref="DeviceScopes"/> scope the grant must hold, or null for any grant.</summary>
    public string? Scope { get; } = scope;
}
