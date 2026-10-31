-- KEYS[1]: Tenant rate limit hash key (e.g. rate:tenant_1042)
-- ARGV[1]: Bucket capacity in tokens (e.g. 50000)
-- ARGV[2]: Token refill rate per millisecond
-- ARGV[3]: Requested tokens to reserve (prompt + max_output)
-- ARGV[4]: Current Unix timestamp in milliseconds

local key = KEYS[1]
local capacity = tonumber(ARGV[1])
local refill_rate = tonumber(ARGV[2])
local requested = tonumber(ARGV[3])
local now = tonumber(ARGV[4])

local data = redis.call('HMGET', key, 'tokens', 'last_refill')
local tokens = tonumber(data[1])
local last_refill = tonumber(data[2])

if tokens == nil then
    tokens = capacity
    last_refill = now
else
    local elapsed = math.max(0, now - last_refill)
    tokens = math.min(capacity, tokens + (elapsed * refill_rate))
    last_refill = now
end

if tokens >= requested then
    tokens = tokens - requested
    redis.call('HMSET', key, 'tokens', tokens, 'last_refill', last_refill)
    return {1, math.floor(tokens)} -- Status: 1 = ALLOWED, remaining tokens
else
    local needed = requested - tokens
    local retry_after_ms = math.ceil(needed / refill_rate)
    return {0, retry_after_ms}     -- Status: 0 = REJECTED, Retry-After in ms
end
