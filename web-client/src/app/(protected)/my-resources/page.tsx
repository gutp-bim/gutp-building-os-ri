import { redirect } from "next/navigation";

// The standalone "マイリソース" card grid duplicated the resource explorer, which is already scoped to
// the resources the signed-in user is authorized for (AuthorizedTwinView). Consolidated into
// /resources (#195); the redirect keeps old links/bookmarks working.
export default function MyResourcesPage() {
  redirect("/resources");
}
