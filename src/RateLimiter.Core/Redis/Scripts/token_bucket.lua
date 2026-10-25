-- Token Bucket rate limiter (atomic refill-and-consume).
--
-- KEYS[1] = ratelimit:tb:{clientId:route}   (hash: tokens, last_refill_ms)
-- ARGV[1] = bucket capacity
-- ARGV[2] = refill rate in tokens per millisecond
-- ARGV[3] = "now" in Unix milliseconds, supplied by the CALLER's TimeProvider
--           (never redis TIME) so tests can control the clock.
-- ARGV[4] = key TTL in milliseconds (cleanup only: a missing key reads as a full bucket)
--
-- Returns { allowed (1 or 0), whole tokens remaining, retry-after in ms (0 if allowed) }

local capacity = tonumber(ARGV[1])
local ratePerMs = tonumber(ARGV[2])
local nowMs = tonumber(ARGV[3])
local ttlMs = tonumber(ARGV[4])

-- Same float-rounding tolerance as the in-memory version.
local EPSILON = 1e-9

local state = redis.call('HMGET', KEYS[1], 'tokens', 'last_refill_ms')
local tokens = tonumber(state[1])
local lastRefillMs = tonumber(state[2])

-- New (or expired) bucket starts FULL.
if tokens == nil or lastRefillMs == nil then
    tokens = capacity
    lastRefillMs = nowMs
end

-- "now" comes from whichever app instance made the call, and instance clocks can
-- drift. Never let a slightly-behind clock produce negative elapsed time, and never
-- move last_refill backwards (that would let another instance re-earn tokens).
local elapsedMs = nowMs - lastRefillMs
if elapsedMs < 0 then elapsedMs = 0 end
local newLastRefillMs = math.max(nowMs, lastRefillMs)

local refilled = math.min(capacity, tokens + (elapsedMs * ratePerMs))

local allowed = 0
local remaining = refilled
local retryAfterMs = 0

if refilled + EPSILON >= 1.0 then
    allowed = 1
    remaining = refilled - 1.0
else
    retryAfterMs = math.ceil((1.0 - refilled) / ratePerMs)
end

-- Explicit %.17g: Redis's default Lua number->string conversion keeps only 14
-- significant digits, which would slowly leak or gain fractional tokens.
redis.call('HSET', KEYS[1],
    'tokens', string.format('%.17g', remaining),
    'last_refill_ms', string.format('%.17g', newLastRefillMs))
redis.call('PEXPIRE', KEYS[1], ttlMs)

return { allowed, math.floor(remaining), retryAfterMs }
