"use server";

import { apiClient } from "@/lib/infra/aspida-client";
import { cookies } from "next/headers";
import { redirect } from "next/navigation";

export default async function Home() {
  const cookieStore = await cookies();
  const token = cookieStore.get("oidc.access_token")?.value;

  let isAdmin = true;
  try {
    const res = await apiClient(token).api.MyResources.$get();
    isAdmin = res.isAdmin ?? true;
  } catch {
    // API失敗時はデフォルトでビル一覧へ
  }

  if (isAdmin) {
    redirect("/buildings");
  } else {
    redirect("/my-resources");
  }
}
