import axiosClient from "@aspida/axios";
import axios from "axios";
import Cookies from "js-cookie";
import api from "./generated/$api";

export const apiClient = (token?: string) => {
  const accessToken = token ?? Cookies.get("oidc.access_token");

  return api(
    axiosClient(axios, {
      baseURL: process.env.API_BASE_URL ?? process.env.NEXT_PUBLIC_API_BASE_URL,
      headers: accessToken
        ? {
          Authorization: `Bearer ${accessToken}`,
        }
        : undefined,
    }),
  );
};
