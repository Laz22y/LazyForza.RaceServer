import type { ControlRole } from "./passwords";

export type ControlPermission = "view" | "manageRace" | "adjudicate" | "manageControlAccounts";

export function controlRoleAllows(role: ControlRole, permission: ControlPermission): boolean {
  if (role === "superAdmin") return true;
  if (role === "administrator") return permission !== "manageControlAccounts";
  return role === "steward" && (permission === "view" || permission === "adjudicate");
}

export function requiredControlPermission(method: string, path: string): ControlPermission {
  const normalized = path.toLowerCase();
  if (normalized.startsWith("/api/admin/control-accounts")) return "manageControlAccounts";
  if (method.toUpperCase() === "GET") return "view";
  if ([
    "/api/admin/penalty",
    "/api/admin/penalty/update",
    "/api/admin/investigation"
  ].includes(normalized)) return "adjudicate";
  return "manageRace";
}
