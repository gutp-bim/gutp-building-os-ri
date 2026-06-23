import SpaceDetailPageComponent from "./page-component";

export default async function SpaceDetailPage(props: {
  params: Promise<{ spaceId: string }>;
}) {
  const params = await props.params;
  return <SpaceDetailPageComponent spaceId={params.spaceId} />;
}
