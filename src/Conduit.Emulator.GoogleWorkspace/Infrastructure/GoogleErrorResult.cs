using Microsoft.AspNetCore.Mvc;
using Conduit.Emulator.GoogleWorkspace.Models;

namespace Conduit.Emulator.GoogleWorkspace.Infrastructure;

public static class GoogleErrorResult
{
    public static IActionResult NotFound(string what, string message)
        => Json(404, new GwErrorEnvelope
        {
            Error = new GwErrorBody
            {
                Code = 404,
                Message = message,
                Status = "NOT_FOUND",
                Errors = new List<GwErrorDetail>
                {
                    new() { Domain = "global", Reason = "notFound", Message = message, Location = what, LocationType = "parameter" }
                }
            }
        });

    public static IActionResult BadRequest(string reason, string message)
        => Json(400, new GwErrorEnvelope
        {
            Error = new GwErrorBody
            {
                Code = 400,
                Message = message,
                Status = "INVALID_ARGUMENT",
                Errors = new List<GwErrorDetail>
                {
                    new() { Domain = "global", Reason = reason, Message = message }
                }
            }
        });

    public static IActionResult Duplicate(string message)
        => Json(409, new GwErrorEnvelope
        {
            Error = new GwErrorBody
            {
                Code = 409,
                Message = message,
                Status = "ALREADY_EXISTS",
                Errors = new List<GwErrorDetail>
                {
                    new() { Domain = "global", Reason = "duplicate", Message = message }
                }
            }
        });

    public static IActionResult PreconditionFailed(string message)
        => Json(412, new GwErrorEnvelope
        {
            Error = new GwErrorBody
            {
                Code = 412,
                Message = message,
                Status = "FAILED_PRECONDITION",
                Errors = new List<GwErrorDetail>
                {
                    new() { Domain = "global", Reason = "conditionNotMet", Message = message, Location = "If-Match", LocationType = "header" }
                }
            }
        });

    public static IActionResult Unauthorized(string message)
        => Json(401, new GwErrorEnvelope
        {
            Error = new GwErrorBody
            {
                Code = 401,
                Message = message,
                Status = "UNAUTHENTICATED",
                Errors = new List<GwErrorDetail>
                {
                    new() { Domain = "global", Reason = "authError", Message = message, Location = "Authorization", LocationType = "header" }
                }
            }
        });

    public static IActionResult Forbidden(string message)
        => Json(403, new GwErrorEnvelope
        {
            Error = new GwErrorBody
            {
                Code = 403,
                Message = message,
                Status = "PERMISSION_DENIED",
                Errors = new List<GwErrorDetail>
                {
                    new() { Domain = "global", Reason = "forbidden", Message = message }
                }
            }
        });

    public static IActionResult Internal(string message)
        => Json(500, new GwErrorEnvelope
        {
            Error = new GwErrorBody
            {
                Code = 500,
                Message = message,
                Status = "INTERNAL",
                Errors = new List<GwErrorDetail> { new() { Reason = "backendError", Message = message } }
            }
        });

    private static IActionResult Json(int status, GwErrorEnvelope body)
        => new ObjectResult(body) { StatusCode = status };
}
