using System.Security.Claims;
using FmCareerChallenge.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FmCareerChallenge.Api.Realtime;

[Authorize]
public sealed class ChallengeHub(ChallengeDbContext database) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(userId, out var parsedUserId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{parsedUserId}");
        }

        await base.OnConnectedAsync();
    }

    public async Task JoinChallenge(string challengeId)
    {
        if (!Guid.TryParse(challengeId, out var parsedChallengeId))
        {
            throw new HubException("The challenge identifier is invalid.");
        }

        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userId, out var parsedUserId))
        {
            throw new HubException("The current user is not authenticated.");
        }

        var isParticipant = await database.ChallengeParticipants
            .AnyAsync(item => item.ChallengeId == parsedChallengeId && item.UserId == parsedUserId);
        if (!isParticipant)
        {
            throw new HubException("You do not have access to this challenge.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"challenge:{parsedChallengeId}");
    }
}
