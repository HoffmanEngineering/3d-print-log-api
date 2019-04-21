using PrintLogApi.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Users
{
    public class UserService
    {
        private readonly PrintLogContext _context;
        public UserService(PrintLogContext context)
        {
            _context = context;
        }

        public User GetLocalUserByAuthUserId(string authUserId)
        {
            return _context.Users.Where(u => u.OAuthUserId == authUserId).FirstOrDefault();
        }

        public async Task<User> CreateUserFromAuthId(string authUserId)
        {
            var newUser = new User
            {
                OAuthUserId = authUserId
            };

            _context.Users.Add(newUser);
            await _context.SaveChangesAsync();

            return newUser;
        }
    }
}
