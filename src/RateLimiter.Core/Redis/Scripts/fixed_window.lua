-- Fixed Window rate limiter (atomic check-and-increment).
--
-- KEYS[1] = ratelimit:fw:{clientId:route}:<windowStartMs>
--           The window start is baked into the key name, so "window rolled over"
--           simply means "a different key" -- no reset logic is needed here.
-- ARGV[1] = permit limit
-- ARGV[2] = key TTL in milliseconds (= window size). Pure cleanup: once a window
--           is over, nothing reads its key again.
--
-- Returns { allowed (1 or 0), count of admitted requests in this window }
--
-- Only ALLOWED requests increment the counter (same design choice as the
-- in-memory version): denied retries never consume quota.

local limit = tonumber(ARGV[1])
local ttlMs = tonumber(ARGV[2])

local count = tonumber(redis.call('GET', KEYS[1]) or '0')

if count >= limit then
    return { 0, count }
end

count = redis.call('INCR', KEYS[1])
if count == 1 then
    redis.call('PEXPIRE', KEYS[1], ttlMs)
end

return { 1, count }
