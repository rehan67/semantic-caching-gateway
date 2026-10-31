-- KEYS[1]: Tenant rate limit hash key (e.g. rate:tenant_1042)
-- ARGV[1]: Bucket capacity in tokens
-- ARGV[2]: Tokens to refund (unconsumed reservation)

local key = KEYS[1]
local capacity = tonumber(ARGV[1])
local refund = tonumber(ARGV[2])

if refund <= 0 then
    return 1
end

local data = redis.call('HGET', key, 'tokens')
local tokens = tonumber(data)

if tokens ~= nil then
    tokens = math.min(capacity, tokens + refund)
    redis.call('HSET', key, 'tokens', tokens)
end

return 1
