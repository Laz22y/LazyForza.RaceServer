import {
  normalizePublicTimingAccess,
  type StoredPublicTimingAccess
} from "./public-timing";

export interface PasswordDigest {
  salt: string;
  hash: string;
  iterations: number;
}

export interface StoredCredentials {
  player: PasswordDigest;
  admin: PasswordDigest;
  controlAccounts?: StoredControlAccount[];
  publicTiming?: StoredPublicTimingAccess;
}

export type ControlRole = "superAdmin" | "administrator" | "steward";

export interface StoredControlAccount {
  id: string;
  name: string;
  role: ControlRole;
  password: PasswordDigest;
  createdAt: string;
  updatedAt: string;
}

export interface ControlPrincipal {
  id: string;
  name: string;
  role: ControlRole;
}

export interface ControlAccountSummary extends ControlPrincipal {
  createdAt: string;
  updatedAt: string;
}

export interface ControlAccountRequest {
  name: string;
  role: ControlRole;
  password?: string | null;
}

export const maximumControlAccounts = 32;

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
  const timestamp = new Date().toISOString();
  return {
    player,
    admin,
    controlAccounts: [{
      id: crypto.randomUUID(),
      name: "初始超管",
      role: "superAdmin",
      password: admin,
      createdAt: timestamp,
      updatedAt: timestamp
    }]
  };
}

export function normalizeStoredCredentials(credentials: StoredCredentials): {
  credentials: StoredCredentials;
  changed: boolean;
} {
  const source = credentials.controlAccounts ?? [];
  const accounts = source.filter(validStoredControlAccount).slice(0, maximumControlAccounts);
  const publicTiming = normalizePublicTimingAccess(credentials.publicTiming);
  const publicTimingChanged = credentials.publicTiming !== undefined && publicTiming === null;
  if (accounts.length > 0) {
    const normalized = { ...credentials, controlAccounts: accounts };
    if (publicTiming) normalized.publicTiming = publicTiming;
    else delete normalized.publicTiming;
    return {
      credentials: normalized,
      changed: accounts.length !== source.length || publicTimingChanged ||
        publicTiming?.generatedAt !== credentials.publicTiming?.generatedAt
    };
  }
  const timestamp = new Date().toISOString();
  return {
    credentials: {
      ...credentials,
      controlAccounts: [{
        id: crypto.randomUUID(),
        name: "初始超管",
        role: "superAdmin",
        password: credentials.admin,
        createdAt: timestamp,
        updatedAt: timestamp
      }],
      ...(publicTiming ? { publicTiming } : {})
    },
    changed: true
  };
}

export async function authenticateControlAccount(
  credentials: StoredCredentials,
  password: string): Promise<ControlPrincipal | null> {
  for (const account of credentials.controlAccounts ?? [])
    if (await verifyPassword(password, account.password))
      return { id: account.id, name: account.name, role: account.role };
  return null;
}

export function controlAccountSummaries(credentials: StoredCredentials): ControlAccountSummary[] {
  return [...(credentials.controlAccounts ?? [])]
    .sort((left, right) => roleOrder(left.role) - roleOrder(right.role) || left.name.localeCompare(right.name))
    .map(({ id, name, role, createdAt, updatedAt }) => ({ id, name, role, createdAt, updatedAt }));
}

export async function createControlAccount(
  credentials: StoredCredentials,
  request: ControlAccountRequest,
  now = new Date()): Promise<{ credentials: StoredCredentials; account: ControlAccountSummary }> {
  const accounts = [...(credentials.controlAccounts ?? [])];
  if (accounts.length >= maximumControlAccounts)
    throw new Error(`总控账号最多保存 ${maximumControlAccounts} 个。`);
  const name = normalizeAccountName(request.name);
  const role = normalizeRole(request.role);
  const password = request.password ?? "";
  if (accounts.some(item => item.name.localeCompare(name, undefined, { sensitivity: "accent" }) === 0))
    throw new Error("总控账号名称不能重复。");
  await validateNewPassword(credentials, accounts, password);
  const timestamp = now.toISOString();
  const stored: StoredControlAccount = {
    id: crypto.randomUUID(), name, role, password: await passwordDigest(password),
    createdAt: timestamp, updatedAt: timestamp
  };
  return {
    credentials: { ...credentials, controlAccounts: [...accounts, stored] },
    account: summary(stored)
  };
}

export async function updateControlAccount(
  credentials: StoredCredentials,
  id: string,
  request: ControlAccountRequest,
  now = new Date()): Promise<{ credentials: StoredCredentials; account: ControlAccountSummary }> {
  const accounts = [...(credentials.controlAccounts ?? [])];
  const index = accounts.findIndex(item => item.id === id);
  if (index < 0) throw new RangeError("总控账号不存在。");
  const previous = accounts[index];
  const name = normalizeAccountName(request.name);
  const role = normalizeRole(request.role);
  if (accounts.some(item => item.id !== id && item.name.localeCompare(name, undefined, { sensitivity: "accent" }) === 0))
    throw new Error("总控账号名称不能重复。");
  if (previous.role === "superAdmin" && role !== "superAdmin" &&
      accounts.filter(item => item.role === "superAdmin").length === 1)
    throw new Error("至少需要保留一个超管账号。");
  let password = previous.password;
  if (request.password) {
    await validateNewPassword(credentials, accounts.filter(item => item.id !== id), request.password);
    password = await passwordDigest(request.password);
  }
  const stored: StoredControlAccount = {
    ...previous, name, role, password, updatedAt: now.toISOString()
  };
  accounts[index] = stored;
  return { credentials: { ...credentials, controlAccounts: accounts }, account: summary(stored) };
}

export function deleteControlAccount(
  credentials: StoredCredentials,
  id: string): { credentials: StoredCredentials; deleted: boolean } {
  const accounts = [...(credentials.controlAccounts ?? [])];
  const account = accounts.find(item => item.id === id);
  if (!account) return { credentials, deleted: false };
  if (account.role === "superAdmin" && accounts.filter(item => item.role === "superAdmin").length === 1)
    throw new Error("至少需要保留一个超管账号。");
  return { credentials: { ...credentials, controlAccounts: accounts.filter(item => item.id !== id) }, deleted: true };
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

export async function passwordDigest(password: string): Promise<PasswordDigest> {
  const salt = crypto.getRandomValues(new Uint8Array(16));
  const key = await crypto.subtle.importKey(
    "raw", new TextEncoder().encode(password), "PBKDF2", false, ["deriveBits"]);
  const hash = new Uint8Array(await crypto.subtle.deriveBits(
    { name: "PBKDF2", hash: "SHA-256", salt, iterations: passwordIterations }, key, 256));
  return { salt: base64Url(salt), hash: base64Url(hash), iterations: passwordIterations };
}

async function validateNewPassword(
  credentials: StoredCredentials,
  accounts: StoredControlAccount[],
  password: string): Promise<void> {
  if (password.length < 8 || password.length > 128) throw new Error("总控密码需要 8–128 个字符。");
  if (await verifyPassword(password, credentials.player)) throw new Error("总控密码不能与房间密码相同。");
  for (const account of accounts)
    if (await verifyPassword(password, account.password))
      throw new Error("每个总控账号必须使用不同密码。");
}

function validStoredControlAccount(value: StoredControlAccount): boolean {
  return Boolean(value && typeof value.id === "string" && typeof value.name === "string" &&
    ["superAdmin", "administrator", "steward"].includes(value.role) && value.password);
}

function normalizeAccountName(value: string): string {
  const name = [...String(value ?? "").trim()].filter(character => character >= " ").slice(0, 48).join("");
  if (!name) throw new Error("总控账号名称不能为空。");
  return name;
}

function normalizeRole(value: ControlRole): ControlRole {
  if (!["superAdmin", "administrator", "steward"].includes(value)) throw new Error("总控角色无效。");
  return value;
}

function roleOrder(role: ControlRole): number {
  return role === "superAdmin" ? 0 : role === "administrator" ? 1 : 2;
}

function summary(account: StoredControlAccount): ControlAccountSummary {
  const { id, name, role, createdAt, updatedAt } = account;
  return { id, name, role, createdAt, updatedAt };
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
