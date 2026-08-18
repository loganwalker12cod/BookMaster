using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BookMaster.Api.Data;
using BookMaster.Api.Models;
using BookMaster.Api.DTOs;

namespace BookMaster.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ExchangeRequestsController : ControllerBase
{
    private readonly AppDbContext _db;
    public ExchangeRequestsController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ExchangeRequestDto>>> GetAll()
    {
        var requests = await _db.ExchangeRequests
            .Select(r => new ExchangeRequestDto(r.Id, r.ListingId, r.RequesterId, r.OfferedBookId, r.Status))
            .ToListAsync();
        return Ok(requests);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ExchangeRequestDto>> GetById(long id)
    {
        var r = await _db.ExchangeRequests.FindAsync(id);
        if (r == null) return NotFound();
        return Ok(new ExchangeRequestDto(r.Id, r.ListingId, r.RequesterId, r.OfferedBookId, r.Status));
    }

    [HttpPost]
    public async Task<ActionResult<ExchangeRequestDto>> Create(CreateExchangeRequestDto dto)
    {
        var listing = await _db.ExchangeListings.Include(l => l.Book).FirstOrDefaultAsync(l => l.Id == dto.ListingId);
        if (listing == null || listing.Book == null) return BadRequest("Listing does not exist.");

        if (listing.Book.Status != BookStatus.Listed)
            return BadRequest("Listing is no longer active.");

        var offeredBook = await _db.Books.FindAsync(dto.OfferedBookId);
        if (offeredBook == null) return BadRequest("Offered book does not exist.");

        if (offeredBook.OwnerId != dto.RequesterId)
            return BadRequest("Requester does not own the offered book.");

        if (listing.Book.OwnerId == dto.RequesterId)
            return BadRequest("Cannot request an exchange on your own listing.");

        var request = new ExchangeRequest
        {
            ListingId = dto.ListingId,
            RequesterId = dto.RequesterId,
            OfferedBookId = dto.OfferedBookId,
            Status = ExchangeRequestStatus.Pending
        };
        _db.ExchangeRequests.Add(request);

        _db.Notifications.Add(new Notification { UserId = listing.Book.OwnerId, IsRead = false });

        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = request.Id }, new ExchangeRequestDto(request.Id, request.ListingId, request.RequesterId, request.OfferedBookId, request.Status));
    }

    [HttpPost("{id}/accept")]
    public async Task<IActionResult> Accept(long id)
    {
        var request = await _db.ExchangeRequests
            .Include(r => r.Listing!).ThenInclude(l => l.Book)
            .Include(r => r.OfferedBook)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (request == null) return NotFound();
        if (request.Status != ExchangeRequestStatus.Pending) return Conflict("Request already processed.");

        var listingBook = request.Listing!.Book!;
        var offeredBook = request.OfferedBook!;

        var useTransaction = _db.Database.IsRelational();
        var transaction = useTransaction ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            var ownerId = listingBook.OwnerId;
            var requesterId = request.RequesterId;

            listingBook.OwnerId = requesterId;
            listingBook.Status = BookStatus.Exchanged;

            offeredBook.OwnerId = ownerId;
            offeredBook.Status = BookStatus.Exchanged;

            request.Status = ExchangeRequestStatus.Accepted;

            // Listing is intentionally kept (not deleted) so History/ExchangeRequest rows
            // referencing it via cascade FKs are preserved. Further requests against it
            // are blocked because both books' Status is now EXCHANGED.

            // reject any other pending requests on the same listing
            var otherRequests = await _db.ExchangeRequests
                .Where(r => r.ListingId == request.ListingId && r.Id != request.Id && r.Status == ExchangeRequestStatus.Pending)
                .ToListAsync();
            foreach (var other in otherRequests)
            {
                other.Status = ExchangeRequestStatus.Rejected;
                _db.Notifications.Add(new Notification { UserId = other.RequesterId, IsRead = false });
            }

            _db.History.Add(new History { RequestId = request.Id, CompletedAt = DateTime.UtcNow });

            _db.Notifications.Add(new Notification { UserId = requesterId, IsRead = false });
            _db.Notifications.Add(new Notification { UserId = ownerId, IsRead = false });

            await _db.SaveChangesAsync();
            if (transaction != null) await transaction.CommitAsync();
        }
        catch
        {
            if (transaction != null) await transaction.RollbackAsync();
            throw;
        }
        finally
        {
            if (transaction != null) await transaction.DisposeAsync();
        }

        return NoContent();
    }

    [HttpPost("{id}/reject")]
    public async Task<IActionResult> Reject(long id)
    {
        var request = await _db.ExchangeRequests.FindAsync(id);
        if (request == null) return NotFound();
        if (request.Status != ExchangeRequestStatus.Pending) return Conflict("Request already processed.");

        request.Status = ExchangeRequestStatus.Rejected;
        _db.Notifications.Add(new Notification { UserId = request.RequesterId, IsRead = false });

        await _db.SaveChangesAsync();
        return NoContent();
    }
}
