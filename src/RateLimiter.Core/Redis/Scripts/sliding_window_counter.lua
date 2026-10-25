-- Sliding Window Counter rate limiter (atomic estimate-and-increment).
--
-- KEYS[1] = current window key  ratelimit:sw:{clientId:route}:<currentWindowStartMs>
-- KEYS[2] = previous window key ratelimit:sw:{clientId:route}:<previousWindowStartMs>
-- ARGV[1] = permit limit
-- ARGV[2] = window size in milliseconds
-- ARGV[3] = milliseconds elapsed since the current window started
--
-- Returns { allowed (1 or 0), current window count after this request, previous window count }
--
-- NOTE: Redis truncates Lua numbers to integers in replies, so the fractional
-- estimate is NOT returned. The caller recomputes it from the two integer counts.

local limit = tonumber(ARGV[1])
local windowMs = tonumber(ARGV[2])
local elapsedMs = tonumber(ARGV[3])

local current = tonumber(redis.call('GET', KEYS[1]) or '0')
local previous = tonumber(redis.call('GET', KEYS[2]) or '0')

-- Fraction of the current window still remaining: the previous window's
-- influence fades from 100% at the boundary to 0% at the end of this window.
local weight = 1 - (elapsedMs / windowMs)
if weight < 0 then weight = 0 elseif weight > 1 then weight = 1 end

local estimated = current + (previous * weight)

if estimated >= limit then
    return { 0, current, previous }
end

current = redis.call('INCR', KEYS[1])
if current == 1 then
    -- 2x window: this key must survive its turn as the "previous" window.
    redis.call('PEXPIRE', KEYS[1], windowMs * 2)
end

return { 1, current, previous }
