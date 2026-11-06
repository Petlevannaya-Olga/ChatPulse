using Microsoft.EntityFrameworkCore;

namespace ChatPulse.Infrastructure.Persistence;

// Схема будет добавлена, когда определим сущности ChatPulse.
public sealed class ChatPulseDbContext(DbContextOptions<ChatPulseDbContext> options)
    : DbContext(options)
{
}

