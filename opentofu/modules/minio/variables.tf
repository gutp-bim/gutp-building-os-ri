variable "namespace" { type = string }
variable "storage_class" { type = string }
variable "storage_size" { type = string }
variable "access_key" { type = string }
variable "secret_key" { type = string; sensitive = true }
variable "image_tag" { type = string; default = "RELEASE.2024-04-18T19-09-19Z" }
variable "labels" { type = map(string); default = {} }
