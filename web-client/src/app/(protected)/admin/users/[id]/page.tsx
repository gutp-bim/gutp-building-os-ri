import { UserDetailClient } from "@/components/admin/user-detail-client";

export default async function AdminUserDetailPage(props: {
  params: Promise<{ id: string }>;
}) {
  const { id } = await props.params;
  return <UserDetailClient id={id} />;
}
