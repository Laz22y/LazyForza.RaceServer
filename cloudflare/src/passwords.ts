export interface PasswordDigest {
  salt: string;
  hash: string;
  iterations: number;
}

export interface StoredCredentials {
  player: PasswordDigest;
  admin: PasswordDigest;
}

const passwordIterations = 120_000;
const minimumCompatibleIterations = 100_000;
const maximumCompatibleIterations = 1_000_000;

export async function createStoredCredentials(
  playerPassword: string,
  adminPassword: string): Promise<StoredCredentials> {
  const [player, admin] = await Promise.all([
    passwordDigest(playerPassword),
    passwordDigest(adminPassword)
  ]);
  return { player, admin };
}

export async function verifyPassword(password: string, digest: PasswordDigest): Promise<boolean> {
  try {
    if (!Number.isInteger(digest.iterations) || digest.iterations <= 0) return false;
    const salt = fromBase64Url(digest.salt);
    const expected = fromBase64Url(digest.hash);
    if (salt.byteLength < 8 || expected.byteLength < 16) return false;
    const key = await crypto.subtle.importKey(
      "raw", new TextEncoder().encode(password), "PBKDF2", false, ["deriveBits"]);
    const actual = new Uint8Array(await crypto.subtle.deriveBits({
      name: "PBKDF2",
      hash: "SHA-256",
      salt,
      iterations: Math.min(
        maximumCompatibleIterations,
        Math.max(minimumCompatibleIterations, digest.iterations))
    }, key, expected.byteLength * 8));
    return timingSafeEqual(actual, expected);
  } catch {
    return false;
  }
}

async function passwordDigest(password: string): Promise<PasswordDigest> {
  const salt = crypto.getRandomValues(new Uint8Array(16));
  const key = await crypto.subtle.importKey(
    "raw", new TextEncoder().encode(password), "PBKDF2", false, ["deriveBits"]);
  const hash = new Uint8Array(await crypto.subtle.deriveBits(
    { name: "PBKDF2", hash: "SHA-256", salt, iterations: passwordIterations }, key, 256));
  return { salt: base64Url(salt), hash: base64Url(hash), iterations: passwordIterations };
}

function timingSafeEqual(left: Uint8Array, right: Uint8Array): boolean {
  if (left.byteLength !== right.byteLength) return false;
  let difference = 0;
  for (let index = 0; index < left.byteLength; index++) difference |= left[index] ^ right[index];
  return difference === 0;
}

function fromBase64Url(value: string): Uint8Array<ArrayBuffer> {
  const normalized = value.replaceAll("-", "+").replaceAll("_", "/");
  const binary = atob(normalized + "=".repeat((4 - normalized.length % 4) % 4));
  const bytes = new Uint8Array(new ArrayBuffer(binary.length));
  for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
  return bytes;
}

function base64Url(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
}
