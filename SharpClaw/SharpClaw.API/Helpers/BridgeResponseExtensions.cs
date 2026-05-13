using System.Diagnostics.CodeAnalysis;
using SharpClaw.Common;

namespace SharpClaw.API.Helpers;

public static class BridgeResponseExtensions
{
    public static bool IsError([NotNullWhen(false)]this BridgeResponse? response) => response?.Status != "ok";

    public static object ToErrorObject(this BridgeResponse? response) => new
    {
        error = response?.ErrorMessage ?? "Bridge execution failed.",
        status = response?.Status ?? "no status",
    };
}