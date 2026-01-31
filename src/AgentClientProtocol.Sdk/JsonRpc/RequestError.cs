// Agent Client Protocol - .NET SDK
// Standard JSON-RPC error codes

using System.Diagnostics;

namespace AgentClientProtocol.Sdk.JsonRpc;

/// <summary>
/// JSON-RPC 2.0 error with standard ACP error codes.
/// </summary>
public class RequestError : Exception
{
    public int Code { get; }
    public new object? Data { get; }
    
    public RequestError(int code, string message, object? data = null) : base(message)
    {
        Code = code;
        Data = data;
    }
    
    // JSON-RPC 2.0 standard errors
    
    [StackTraceHidden]
    public static RequestError ParseError(object? data = null, string? additional = null) =>
        new(-32700, $"Parse error{(additional != null ? $": {additional}" : "")}", data);
    
    [StackTraceHidden]
    public static RequestError InvalidRequest(object? data = null, string? additional = null) =>
        new(-32600, $"Invalid Request{(additional != null ? $": {additional}" : "")}", data);
    
    [StackTraceHidden]
    public static RequestError MethodNotFound(string method) =>
        new(-32601, $"Method not found: {method}");
    
    [StackTraceHidden]
    public static RequestError InvalidParams(object? data = null, string? additional = null) =>
        new(-32602, $"Invalid params{(additional != null ? $": {additional}" : "")}", data);
    
    [StackTraceHidden]
    public static RequestError InternalError(object? data = null, string? additional = null) =>
        new(-32603, $"Internal error{(additional != null ? $": {additional}" : "")}", data);
    
    // ACP-specific errors (reserved range -32000 to -32099)
    
    [StackTraceHidden]
    public static RequestError AuthRequired(object? data = null, string? additional = null) =>
        new(-32000, $"Authentication required{(additional != null ? $": {additional}" : "")}", data);
    
    [StackTraceHidden]
    public static RequestError ResourceNotFound(string? uri = null) =>
        new(-32002, $"Resource not found{(uri != null ? $": {uri}" : "")}", uri != null ? new { uri } : null);
    
    [StackTraceHidden]
    public static RequestError Cancelled() =>
        new(-32800, "Request cancelled");
    
    public JsonRpcError ToError() => new()
    {
        Code = Code,
        Message = Message,
        Data = Data
    };
    
    public JsonRpcResponse ToResponse(object? id) => 
        JsonRpcResponse.Failure(id, ToError());
}
