import PointDetailPageComponent from "./page-component";

export default async function PointDetailPage(props: {
  params: Promise<{ pointId: string }>;
}) {
  const params = await props.params;
  return <PointDetailPageComponent pointId={params.pointId} />;
}
