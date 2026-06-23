variable "namespace" { type = string }
variable "storage_class" { type = string }
variable "storage_size" { type = string }
variable "replicas" { type = number; default = 1 }
variable "image_tag" { type = string; default = "2.10-alpine" }
variable "labels" { type = map(string); default = {} }
