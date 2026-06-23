import { TableProvider } from "@/contexts/TableContext";
import DeviceDetailPageComponent from "./page-component";

export default async function DeviceDetailPage(props: {
  params: Promise<{ deviceId: string }>;
}) {
  const params = await props.params;
  return (
    <TableProvider
      fields={["name", "dataSpecification", "dataType", "writable", "targetArea"]}
    >
      <DeviceDetailPageComponent deviceId={params.deviceId} />
    </TableProvider>
  );
}
