/** 解包「JSON 字符串里再塞 JSON」的重复编码（最多两层）。 */
export function unwrapJsonValue(value, maxDepth = 2) {
  let current = value
  for (let i = 0; i < maxDepth; i++) {
    if (typeof current !== 'string') break
    const trimmed = current.trim()
    if (!trimmed.startsWith('{') && !trimmed.startsWith('[')) break
    try {
      current = JSON.parse(trimmed)
    } catch {
      break
    }
  }
  return current
}

/** @returns {{ ok: true, value: unknown } | { ok: false, text: string }} */
export function parseJsonText(text) {
  const raw = text == null ? '' : String(text).trim()
  if (!raw) return { ok: false, text: '' }

  try {
    return { ok: true, value: unwrapJsonValue(JSON.parse(raw)) }
  } catch {
    return { ok: false, text: String(text) }
  }
}

/** C# 桥接可能直接下发已解析对象，或仍是 JSON 字符串。 */
export function coerceJsonPayload(payload) {
  if (payload == null) return { ok: false, text: '' }
  if (typeof payload === 'object') return { ok: true, value: unwrapJsonValue(payload) }
  return parseJsonText(payload)
}
