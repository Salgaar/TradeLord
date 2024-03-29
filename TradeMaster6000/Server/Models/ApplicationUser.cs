﻿using Microsoft.AspNetCore.Identity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;

namespace TradeMaster6000.Server.Models
{
    public class ApplicationUser : IdentityUser
    {
        public string ApiKey { get; set; } = null;
        public string AppSecret { get; set; } = null;
    }
}
