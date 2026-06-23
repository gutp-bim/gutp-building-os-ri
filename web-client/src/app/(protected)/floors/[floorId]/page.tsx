import FloorDetailPageComponent from "./page-component";

export default async function FloorDetailPage(props: {
  params: Promise<{ floorId: string }>;
}) {
  const params = await props.params;
  return <FloorDetailPageComponent floorId={params.floorId} />;
}
