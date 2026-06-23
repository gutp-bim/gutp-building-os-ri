import BuildingDetailPageComponent from "./page-component";

export default async function BuildingDetailPage(props: {
  params: Promise<{ buildingId: string }>;
}) {
  const params = await props.params;
  return <BuildingDetailPageComponent buildingId={params.buildingId} />;
}
