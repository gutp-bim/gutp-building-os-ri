import { GroupDetailClient } from "@/components/admin/group-detail-client";

export default async function AdminGroupDetailPage(props: {
  params: Promise<{ id: string }>;
}) {
  const { id } = await props.params;
  return <GroupDetailClient id={id} />;
}
