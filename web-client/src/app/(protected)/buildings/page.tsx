import { redirect } from "next/navigation";

// The buildings list is replaced by the resource explorer (#UI-improve). The detail deep link
// /buildings/[buildingId] is unchanged.
export default function BuildingsPage() {
  redirect("/resources");
}
